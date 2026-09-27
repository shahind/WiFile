using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Web.Script.Serialization;

namespace WiFile.Core
{
    public sealed class ChatMessage
    {
        public string Id { get; set; }
        public string From { get; set; }
        public string FromName { get; set; }
        public string To { get; set; }        // null = everyone
        public string ToName { get; set; }
        public long Time { get; set; }        // unix ms
        public string Text { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string LocalPath { get; set; } // local only: where the attachment lives on this PC
        public bool Outgoing { get; set; }    // local only
        [ScriptIgnore] public string Status { get; set; }
        [ScriptIgnore] public bool IsFile => FileName != null;
        [ScriptIgnore] public DateTime LocalTime => DateTimeOffset.FromUnixTimeMilliseconds(Time).LocalDateTime;

        public Dictionary<string, object> ToWire() => new Dictionary<string, object>
        {
            ["id"] = Id, ["from"] = From, ["fromName"] = FromName, ["to"] = To, ["toName"] = ToName,
            ["time"] = Time, ["text"] = Text, ["fileName"] = FileName, ["fileSize"] = FileSize,
        };

        public static ChatMessage FromWire(Dictionary<string, object> d) => new ChatMessage
        {
            Id = d.Str("id") ?? Guid.NewGuid().ToString("N"),
            From = d.Str("from"), FromName = d.Str("fromName") ?? "Unknown",
            To = d.Str("to"), ToName = d.Str("toName"),
            Time = d.Long("time"), Text = d.Str("text"),
            FileName = d.Str("fileName"), FileSize = d.Long("fileSize"),
        };
    }

    /// <summary>
    /// Direct peer-to-peer chat. A message (plus optional attachment bytes) is pushed straight
    /// to each online recipient; attachments land in the receive folder (Downloads\WiFile).
    /// </summary>
    public sealed class ChatService
    {
        readonly Func<string> _myId, _myName;
        readonly Func<List<Peer>> _peers;
        readonly Func<string, Peer> _findPeer;
        readonly Func<Peer, TcpClient> _connect;
        readonly string _historyFile;
        readonly object _gate = new object();
        readonly HashSet<string> _receiving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string ReceiveDir { get; }
        public event Action<ChatMessage> MessageReceived;
        public event Action<ChatMessage> MessageUpdated;

        public ChatService(Func<string> myId, Func<string> myName, Func<List<Peer>> peers, Func<string, Peer> findPeer,
            string receiveDir, string historyFile, Func<Peer, TcpClient> connect = null)
        {
            _connect = connect ?? (p => Wire.Connect(p.EndPoint));
            _myId = myId;
            _myName = myName;
            _peers = peers;
            _findPeer = findPeer;
            ReceiveDir = receiveDir;
            _historyFile = historyFile;
        }

        public List<ChatMessage> LoadHistory(int max = 200)
        {
            var list = new List<ChatMessage>();
            try
            {
                if (!File.Exists(_historyFile)) return list;
                foreach (var line in File.ReadAllLines(_historyFile).Reverse().Take(max).Reverse())
                {
                    try
                    {
                        var m = Json.Deserialize<ChatMessage>(line);
                        if (m?.Id != null) list.Add(m);
                    }
                    catch { }
                }
                // Keep the history file bounded.
                var all = File.ReadAllLines(_historyFile);
                if (all.Length > 2000) File.WriteAllLines(_historyFile, all.Skip(all.Length - 1000));
            }
            catch (Exception ex) { Log.Error("chat history load", ex); }
            return list;
        }

        void Persist(ChatMessage m)
        {
            lock (_gate)
            {
                try { File.AppendAllText(_historyFile, Json.Serialize(m) + Environment.NewLine); }
                catch (Exception ex) { Log.Error("chat history save", ex); }
            }
        }

        public ChatMessage SendText(string text, string toId) => Send(NewMessage(toId, m => m.Text = text), null);

        public ChatMessage SendFile(string path, string toId)
        {
            var fi = new FileInfo(path);
            return Send(NewMessage(toId, m =>
            {
                m.FileName = fi.Name;
                m.FileSize = fi.Length;
                m.LocalPath = fi.FullName;
            }), fi.FullName);
        }

        ChatMessage NewMessage(string toId, Action<ChatMessage> fill)
        {
            var m = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                From = _myId(),
                FromName = _myName(),
                To = toId,
                ToName = _findPeer(toId)?.Name,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Outgoing = true,
            };
            fill(m);
            return m;
        }

        ChatMessage Send(ChatMessage m, string attachment)
        {
            Persist(m);
            var targets = m.To == null ? _peers() : new[] { _findPeer(m.To) }.Where(p => p != null).ToList();
            if (targets.Count == 0)
            {
                m.Status = "Not delivered \u2014 no one else is online";
                return m;
            }
            m.Status = m.IsFile ? "Sending\u2026" : "";
            int ok = 0, left = targets.Count;
            foreach (var peer in targets)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    // A few retries ride out Wi-Fi hiccups and a peer that is just reconnecting.
                    for (int attempt = 1; attempt <= 4; attempt++)
                    {
                        try
                        {
                            var target = _findPeer(peer.Id) ?? peer;
                            SendTo(target, m, attachment);
                            Interlocked.Increment(ref ok);
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"chat send to {peer} (attempt {attempt})", ex);
                            if (attempt < 4) Thread.Sleep(2000 * attempt);
                        }
                    }
                    if (Interlocked.Decrement(ref left) == 0)
                    {
                        m.Status = ok == targets.Count
                            ? (targets.Count == 1 ? "Delivered" : $"Delivered to {ok}")
                            : ok == 0 ? "Not delivered" : $"Delivered to {ok} of {targets.Count}";
                        MessageUpdated?.Invoke(m);
                    }
                });
            }
            return m;
        }

        void SendTo(Peer peer, ChatMessage m, string attachment)
        {
            using (var c = _connect(peer))
            {
                var s = c.GetStream();
                if (attachment == null)
                {
                    Wire.Send(s, new Dictionary<string, object> { ["t"] = "chat", ["msg"] = m.ToWire(), ["len"] = 0 });
                }
                else
                {
                    using (var fs = new FileStream(attachment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    {
                        Wire.Send(s, new Dictionary<string, object> { ["t"] = "chat", ["msg"] = m.ToWire(), ["len"] = fs.Length });
                        Wire.Copy(fs, s, fs.Length);
                    }
                }
                var r = Wire.Receive(s);
                if (!r.Bool("ok")) throw new IOException(r.Str("err") ?? "rejected");
            }
        }

        public void Receive(NetworkStream s, Dictionary<string, object> h)
        {
            var m = ChatMessage.FromWire(h["msg"] as Dictionary<string, object>);
            long len = h.Long("len");
            if (m.FileName != null)
            {
                Directory.CreateDirectory(ReceiveDir);
                string dest, part;
                lock (_gate)
                {
                    var name = PathUtil.SafeFileName(m.FileName);
                    dest = PathUtil.UniquePath(ReceiveDir, name);
                    for (int i = 1; _receiving.Contains(dest); i++)
                        dest = PathUtil.UniquePath(ReceiveDir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                    _receiving.Add(dest); // reserve the name while bytes arrive
                    part = dest + ".wifile-part";
                }
                try
                {
                    using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        Wire.Copy(s, fs, len);
                    File.Move(part, dest);
                }
                catch
                {
                    try { File.Delete(part); } catch { }
                    throw;
                }
                finally
                {
                    lock (_gate) _receiving.Remove(dest);
                }
                m.LocalPath = dest;
                m.FileSize = len;
            }
            Wire.Send(s, new Dictionary<string, object> { ["ok"] = true });
            Persist(m);
            MessageReceived?.Invoke(m);
        }
    }
}
