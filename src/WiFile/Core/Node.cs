using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace WiFile.Core
{
    public sealed class NodeConfig
    {
        public string SharedDir;
        public string DataDir;
        public string ReceiveDir;
        public int DiscoveryPort = 45877;
        public int TcpPort = 45878;
        public bool UseBroadcast = true;
        /// <summary>Testing aid: advertise this TCP port instead of the real one (simulates a firewalled PC).</summary>
        public int AdvertisedPortOverride;
        public List<IPEndPoint> ExtraBeaconTargets = new List<IPEndPoint>();

        public static NodeConfig Default(string profile = null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var suffix = string.IsNullOrEmpty(profile) ? "" : "-" + profile;
            return new NodeConfig
            {
                SharedDir = Path.Combine(home, "WiFile" + suffix),
                DataDir = Path.Combine(local, "WiFile" + suffix),
                ReceiveDir = Path.Combine(home, "Downloads", "WiFile" + suffix),
                // Test profiles form their own separate network so they never mix with real devices.
                DiscoveryPort = string.IsNullOrEmpty(profile) ? 45877 : 45977,
                TcpPort = string.IsNullOrEmpty(profile) ? 45878 : 0,
            };
        }
    }

    public sealed class Settings
    {
        public string DeviceId { get; set; }
        public string Name { get; set; }
        public bool AutoStartConfigured { get; set; }
        public bool TrayHintShown { get; set; }
        public string ViewMode { get; set; }

        [System.Web.Script.Serialization.ScriptIgnore] public string File { get; private set; }

        public static Settings Load(string file)
        {
            Settings s = null;
            try { if (System.IO.File.Exists(file)) s = Json.Deserialize<Settings>(System.IO.File.ReadAllText(file)); }
            catch (Exception ex) { Log.Error("settings load", ex); }
            s = s ?? new Settings();
            s.File = file;
            bool save = false;
            if (string.IsNullOrEmpty(s.DeviceId)) { s.DeviceId = Guid.NewGuid().ToString("N"); save = true; }
            if (string.IsNullOrEmpty(s.Name)) { s.Name = DefaultName(); save = true; }
            if (save) s.Save();
            return s;
        }

        static string DefaultName()
        {
            var user = Environment.UserName ?? "User";
            if (user.Length > 0) user = char.ToUpper(user[0]) + user.Substring(1);
            return $"{user} ({Environment.MachineName})";
        }

        public void Save()
        {
            try { System.IO.File.WriteAllText(File, Json.Serialize(this)); }
            catch (Exception ex) { Log.Error("settings save", ex); }
        }
    }

    /// <summary>One WiFile participant: discovery + server + folder sync + chat.</summary>
    public sealed class WiFileNode : IDisposable
    {
        public NodeConfig Config { get; }
        public Settings Settings { get; }
        public Discovery Discovery { get; private set; }
        public PeerServer Server { get; private set; }
        public SyncEngine Sync { get; private set; }
        public ChatService Chat { get; private set; }
        public Connector Connector { get; private set; }

        public string Id => Settings.DeviceId;
        public string Name => Settings.Name;

        public WiFileNode(NodeConfig cfg)
        {
            Config = cfg;
            Directory.CreateDirectory(cfg.DataDir);
            Log.Init(cfg.DataDir);
            Settings = Settings.Load(Path.Combine(cfg.DataDir, "settings.json"));
        }

        public void Start()
        {
            Log.Info($"Starting WiFile node {Name} [{Id}] shared={Config.SharedDir}");
            Connector = new Connector(Id, () => Discovery, () => Server.Port, Dispatch);
            Sync = new SyncEngine(Config.SharedDir, Config.DataDir, Id, () => Discovery.Peers, id => Discovery.Find(id), Connector.Connect);
            Chat = new ChatService(() => Id, () => Settings.Name, () => Discovery.Peers, id => Discovery.Find(id),
                Config.ReceiveDir, Path.Combine(Config.DataDir, "chat.jsonl"), Connector.Connect);
            Server = new PeerServer(Serve);
            Discovery = new Discovery(Config, Id, Beacon);
            Discovery.ReverseRequested += (id, from, token, port) => Connector.HandleReverseRequest(id, from, token, port);

            Discovery.PeerUpdated += p => Sync.Poke();
            Sync.IndexAdvanced += () => Discovery.Announce();

            Server.Start(Config.TcpPort);
            Sync.Start();
            Discovery.Start();
        }

        Dictionary<string, object> Beacon() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["name"] = Settings.Name,
            ["port"] = Config.AdvertisedPortOverride > 0 ? Config.AdvertisedPortOverride : Server.Port,
            ["epoch"] = Sync.Index.Epoch,
            ["seq"] = Sync.Index.Seq,
            ["v"] = 2,
        };

        bool Serve(TcpClient c, NetworkStream s, Dictionary<string, object> h)
        {
            if (h.Str("t") == "reverse") return Connector.AcceptReverse(c, h.Str("token"));
            Dispatch(s, h);
            return false;
        }

        void Dispatch(NetworkStream s, Dictionary<string, object> h)
        {
            switch (h.Str("t"))
            {
                case "index": Sync.ServeIndex(s, h); break;
                case "get": Sync.ServeFile(s, h); break;
                case "chat": Chat.Receive(s, h); break;
                case "chatdel": Chat.ReceiveDelete(s, h); break;
                default: Wire.Send(s, Wire.Error("unknown request")); break;
            }
        }

        public void Rename(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == Settings.Name) return;
            Settings.Name = name.Length > 40 ? name.Substring(0, 40) : name;
            Settings.Save();
            Discovery?.Announce();
        }

        public void Dispose()
        {
            Log.Info("Stopping");
            Discovery?.Dispose();
            Server?.Dispose();
            Sync?.Dispose();
        }
    }
}
