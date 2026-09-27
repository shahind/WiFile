using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WiFile.Core
{
    /// <summary>
    /// Opens a request connection to a peer. If the peer's firewall blocks incoming TCP
    /// (a very common Windows situation), it falls back to a "reverse" connection: we ask the
    /// peer over UDP to dial *us*, and the request then runs over that socket. So two PCs can
    /// talk as long as either one of them accepts connections.
    /// </summary>
    public sealed class Connector
    {
        static readonly TimeSpan DirectRetry = TimeSpan.FromSeconds(60);

        readonly ConcurrentDictionary<string, TaskCompletionSource<TcpClient>> _waiting =
            new ConcurrentDictionary<string, TaskCompletionSource<TcpClient>>();
        readonly Func<Discovery> _discovery;
        readonly Func<int> _myPort;
        readonly string _myId;
        readonly Action<NetworkStream, Dictionary<string, object>> _dispatch;

        /// <summary>Raised when a peer had to ask us for a reverse connection, i.e. our inbound TCP is blocked.</summary>
        public event Action InboundBlocked;

        public Connector(string myId, Func<Discovery> discovery, Func<int> myPort, Action<NetworkStream, Dictionary<string, object>> dispatch)
        {
            _myId = myId;
            _discovery = discovery;
            _myPort = myPort;
            _dispatch = dispatch;
        }

        public TcpClient Connect(Peer p)
        {
            bool tryDirect = !p.UseReverse || DateTime.UtcNow - p.ReverseSince > DirectRetry;
            Exception direct = null;
            if (tryDirect)
            {
                try
                {
                    var c = Wire.Connect(p.EndPoint, p.SupportsReverse ? 2500 : 4000);
                    if (p.UseReverse) Log.Info("Direct connection to " + p.Name + " works again");
                    p.UseReverse = false;
                    return c;
                }
                catch (Exception ex)
                {
                    direct = ex;
                    if (!p.SupportsReverse) throw;
                    if (!p.UseReverse) Log.Info($"Can't reach {p.Name} directly ({ex.Message}); using reverse connections");
                    p.UseReverse = true;
                    p.ReverseSince = DateTime.UtcNow;
                }
            }
            return ConnectReverse(p) ?? throw new IOException($"{p.Name} is unreachable", direct);
        }

        TcpClient ConnectReverse(Peer p)
        {
            if (p.UdpEndPoint == null) return null;
            var token = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<TcpClient>();
            _waiting[token] = tcs;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    _discovery().SendReverseRequest(p.UdpEndPoint, token, _myPort());
                    if (tcs.Task.Wait(1500)) return tcs.Task.Result;
                }
                return null;
            }
            finally { _waiting.TryRemove(token, out _); }
        }

        /// <summary>Server side: a peer dialed back in answer to our reverse request. Returns true if we took the socket.</summary>
        public bool AcceptReverse(TcpClient client, string token)
        {
            if (token != null && _waiting.TryRemove(token, out var tcs))
                return tcs.TrySetResult(client);
            return false;
        }

        /// <summary>A peer can't reach us: dial it and serve its request over our outgoing socket.</summary>
        public void HandleReverseRequest(string peerId, IPEndPoint from, string token, int port)
        {
            var p = _discovery().Find(peerId);
            if (p == null || !p.Address.Equals(from.Address) || port <= 0 || port > 65535) return;
            InboundBlocked?.Invoke();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var c = Wire.Connect(new IPEndPoint(from.Address, port)))
                    {
                        var s = c.GetStream();
                        Wire.Send(s, new Dictionary<string, object> { ["t"] = "reverse", ["token"] = token, ["id"] = _myId });
                        var request = Wire.Receive(s);
                        _dispatch(s, request);
                    }
                }
                catch (Exception ex) { Log.Error("reverse connection to " + p, ex); }
            });
        }
    }
}
