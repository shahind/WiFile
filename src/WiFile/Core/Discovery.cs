using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WiFile.Core
{
    public sealed class Peer
    {
        public string Id;
        public string Name;
        public IPAddress Address;
        public int Port;
        public string Epoch;
        public long Seq;
        public DateTime LastSeen;
        public IPEndPoint UdpEndPoint;
        public bool SupportsReverse;              // protocol v2+
        public volatile bool UseReverse;          // direct TCP to this peer is blocked
        public DateTime ReverseSince;
        public IPEndPoint EndPoint => new IPEndPoint(Address, Port);
        public override string ToString() => $"{Name} ({Address}:{Port})";
    }

    /// <summary>
    /// Zero-config discovery: every node broadcasts a small UDP beacon every 2 s on each
    /// network interface. A beacon carries the node's TCP port and index version so peers
    /// know when to pull changes. Peers silent for 8 s are considered gone.
    /// </summary>
    public sealed class Discovery : IDisposable
    {
        const string Magic = "WIFILE1";
        const string ReverseMagic = "WIFILEREV";
        static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(8);

        readonly NodeConfig _cfg;
        readonly string _myId;
        readonly Func<Dictionary<string, object>> _beacon;
        readonly Dictionary<string, Peer> _peers = new Dictionary<string, Peer>();
        UdpClient _udp;
        Thread _rx;
        Timer _timer;
        volatile bool _running;
        List<IPEndPoint> _targets = new List<IPEndPoint>();
        DateTime _targetsAt = DateTime.MinValue;

        /// <summary>Peer list or names changed.</summary>
        public event Action PeersChanged;
        /// <summary>A peer is new or advertised a new index version.</summary>
        public event Action<Peer> PeerUpdated;
        /// <summary>A peer can't connect to us and asks us to dial it: (peerId, from, token, tcpPort).</summary>
        public event Action<string, IPEndPoint, string, int> ReverseRequested;

        public Discovery(NodeConfig cfg, string myId, Func<Dictionary<string, object>> beacon)
        {
            _cfg = cfg;
            _myId = myId;
            _beacon = beacon;
        }

        public List<Peer> Peers
        {
            get { lock (_peers) return _peers.Values.OrderBy(p => p.Name).ToList(); }
        }

        public Peer Find(string id)
        {
            if (id == null) return null;
            lock (_peers) return _peers.TryGetValue(id, out var p) ? p : null;
        }

        public void Start()
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.DiscoveryPort));
            _udp.EnableBroadcast = true;
            // Stop ICMP "port unreachable" from killing Receive() with WSAECONNRESET.
            try { _udp.Client.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch { }
            _running = true;
            _rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "WiFile-Discovery" };
            _rx.Start();
            _timer = new Timer(_ => Tick(), null, 0, 2000);
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }

        void OnNetworkChanged(object sender, EventArgs e)
        {
            _targetsAt = DateTime.MinValue;
            Announce();
        }

        /// <summary>Send a beacon right now (e.g. after a local change).</summary>
        public void Announce() => ThreadPool.QueueUserWorkItem(_ => SendBeacon(null));

        void Tick()
        {
            if (!_running) return;
            SendBeacon(null);
            Expire();
        }

        void SendBeacon(IPEndPoint only)
        {
            if (!_running) return;
            byte[] data;
            try { data = Encoding.UTF8.GetBytes(Magic + Json.Serialize(_beacon())); }
            catch (Exception ex) { Log.Error("beacon build", ex); return; }
            var targets = only != null ? new List<IPEndPoint> { only } : Targets();
            foreach (var t in targets)
            {
                try { _udp.Send(data, data.Length, t); }
                catch { /* interface may be down */ }
            }
        }

        List<IPEndPoint> Targets()
        {
            if (DateTime.UtcNow - _targetsAt < TimeSpan.FromSeconds(15)) return _targets;
            var list = new List<IPEndPoint>();
            if (_cfg.UseBroadcast)
            {
                list.Add(new IPEndPoint(IPAddress.Broadcast, _cfg.DiscoveryPort));
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                            var a = ua.Address.GetAddressBytes();
                            var m = ua.IPv4Mask.GetAddressBytes();
                            if (m.All(b => b == 0)) continue;
                            var b2 = new byte[4];
                            for (int i = 0; i < 4; i++) b2[i] = (byte)(a[i] | ~m[i]);
                            var ep = new IPEndPoint(new IPAddress(b2), _cfg.DiscoveryPort);
                            if (!list.Any(x => x.Equals(ep))) list.Add(ep);
                        }
                    }
                }
                catch (Exception ex) { Log.Error("interface enumeration", ex); }
            }
            list.AddRange(_cfg.ExtraBeaconTargets);
            _targets = list;
            _targetsAt = DateTime.UtcNow;
            return list;
        }

        void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref from);
                    Handle(data, from);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (!_running) break; }
                catch (Exception ex) { Log.Error("discovery receive", ex); }
            }
        }

        public void SendReverseRequest(IPEndPoint to, string token, int tcpPort)
        {
            var msg = new Dictionary<string, object> { ["id"] = _myId, ["token"] = token, ["port"] = tcpPort };
            var data = Encoding.UTF8.GetBytes(ReverseMagic + Json.Serialize(msg));
            try { _udp.Send(data, data.Length, to); } catch (Exception ex) { Log.Error("reverse request", ex); }
        }

        void Handle(byte[] data, IPEndPoint from)
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.StartsWith(ReverseMagic))
            {
                var r = Json.Parse(text.Substring(ReverseMagic.Length));
                ReverseRequested?.Invoke(r.Str("id"), from, r.Str("token"), (int)r.Long("port"));
                return;
            }
            if (!text.StartsWith(Magic)) return;
            var d = Json.Parse(text.Substring(Magic.Length));
            var id = d.Str("id");
            if (string.IsNullOrEmpty(id) || id == _myId) return;

            bool isNew = false, nameChanged = false, versionChanged = false;
            Peer p;
            lock (_peers)
            {
                if (!_peers.TryGetValue(id, out p))
                {
                    p = new Peer { Id = id };
                    _peers[id] = p;
                    isNew = true;
                }
                var name = d.Str("name") ?? "Unknown";
                if (p.Name != name) { p.Name = name; nameChanged = true; }
                var epoch = d.Str("epoch");
                var seq = d.Long("seq");
                if (p.Epoch != epoch || p.Seq != seq) versionChanged = true;
                if (p.Address != null && !p.Address.Equals(from.Address)) p.UseReverse = false;
                p.Address = from.Address;
                p.UdpEndPoint = from;
                p.SupportsReverse = d.Long("v") >= 2;
                p.Port = (int)d.Long("port");
                p.Epoch = epoch;
                p.Seq = seq;
                p.LastSeen = DateTime.UtcNow;
            }
            if (isNew)
            {
                Log.Info("Peer joined: " + p);
                SendBeacon(from); // let the newcomer discover us immediately
            }
            if (isNew || nameChanged) PeersChanged?.Invoke();
            if (isNew || versionChanged) PeerUpdated?.Invoke(p);
        }

        void Expire()
        {
            List<Peer> gone;
            lock (_peers)
            {
                gone = _peers.Values.Where(p => DateTime.UtcNow - p.LastSeen > PeerTimeout).ToList();
                foreach (var p in gone) _peers.Remove(p.Id);
            }
            foreach (var p in gone) Log.Info("Peer left: " + p);
            if (gone.Count > 0) PeersChanged?.Invoke();
        }

        public void Dispose()
        {
            _running = false;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _timer?.Dispose();
            try { _udp?.Close(); } catch { }
        }
    }
}
