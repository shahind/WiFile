using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace WiFile.Core
{
    public sealed class SyncStatus
    {
        public bool Busy;
        public string Text;
        public int Pending;
    }

    /// <summary>
    /// Keeps the local shared folder identical to every peer's copy.
    ///
    /// Model: each node keeps an index (path -> version). A local scan turns disk changes into
    /// new versions stamped with a hybrid clock; peers pull each other's index deltas and apply
    /// any version newer than their own (last writer wins). Deletions are tombstones.
    /// All disk mutation happens on a single sync thread, so there are no local races.
    /// </summary>
    public sealed class SyncEngine : IDisposable
    {
        sealed class Pending
        {
            public FileEntry Entry;
            public readonly HashSet<string> Sources = new HashSet<string>();
            public int Attempts;
            public DateTime NotBefore;
        }

        sealed class Cursor { public string Epoch; public long Seq; }

        sealed class TrashItem { public string Path; public long Size; public string Hash; public DateTime At; }

        static readonly TimeSpan FullScanInterval = TimeSpan.FromSeconds(30);
        static readonly TimeSpan TrashKeep = TimeSpan.FromMinutes(5);

        readonly string _root, _trashDir, _tmpDir, _myId;
        readonly Func<List<Peer>> _peers;
        readonly Func<string, Peer> _findPeer;
        readonly Func<Peer, TcpClient> _connect;
        readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Cursor> _cursors = new Dictionary<string, Cursor>();
        readonly List<TrashItem> _trash = new List<TrashItem>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        FileSystemWatcher _watcher;
        Thread _thread;
        volatile bool _running;
        volatile bool _scanRequested = true;
        long _lastFsEventTicks;
        DateTime _lastScan = DateTime.MinValue, _lastAnnounce = DateTime.MinValue, _lastPurge = DateTime.MinValue;
        string _lastStatus;

        public FileIndex Index { get; }
        public SyncStatus CurrentStatus { get; private set; } = new SyncStatus { Busy = true, Text = "Starting\u2026" };
        public string Root => _root;

        /// <summary>Local index sequence advanced; peers should be told.</summary>
        public event Action IndexAdvanced;
        public event Action<SyncStatus> StatusChanged;
        /// <summary>A file or folder changed on disk (full path, is folder, removed). Lets the UI refresh instantly.</summary>
        public event Action<string, bool, bool> ItemChanged;

        public SyncEngine(string root, string dataDir, string myId, Func<List<Peer>> peers, Func<string, Peer> findPeer,
            Func<Peer, TcpClient> connect = null)
        {
            _connect = connect ?? (p => Wire.Connect(p.EndPoint));
            _root = Path.GetFullPath(root);
            _myId = myId;
            _peers = peers;
            _findPeer = findPeer;
            _trashDir = Path.Combine(dataDir, "trash");
            _tmpDir = Path.Combine(dataDir, "tmp");
            Index = new FileIndex(Path.Combine(dataDir, "index.dat"));
        }

        public void Start()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_tmpDir);
            Directory.CreateDirectory(_trashDir);
            foreach (var f in Directory.GetFiles(_tmpDir)) TryDelete(f);
            foreach (var d in Directory.GetDirectories(_trashDir)) Recycle(d);
            Index.Load();

            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler onChange = (s, e) => OnFsEvent();
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (s, e) => OnFsEvent();
            _watcher.Error += (s, e) => OnFsEvent();
            _watcher.EnableRaisingEvents = true;

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "WiFile-Sync" };
            _thread.Start();
        }

        void OnFsEvent()
        {
            Interlocked.Exchange(ref _lastFsEventTicks, DateTime.UtcNow.Ticks);
            _scanRequested = true;
        }

        /// <summary>Wake the sync thread (a peer advertised new changes).</summary>
        public void Poke() => _wake.Set();

        public void RequestScan()
        {
            _scanRequested = true;
            _wake.Set();
        }

        void Loop()
        {
            while (_running)
            {
                _wake.WaitOne(700);
                if (!_running) break;
                try
                {
                    bool quiet = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFsEventTicks) > TimeSpan.FromMilliseconds(600).Ticks;
                    if ((_scanRequested && quiet) || DateTime.UtcNow - _lastScan > FullScanInterval)
                    {
                        _scanRequested = false;
                        Scan();
                        _lastScan = DateTime.UtcNow;
                    }
                    Pull();
                    ProcessPending();
                    if (DateTime.UtcNow - _lastPurge > TimeSpan.FromSeconds(30)) PurgeTrash();
                    Flush(true);
                }
                catch (Exception ex) { Log.Error("sync loop", ex); }
            }
        }

        long _announcedSeq;

        /// <summary>Persist the index and announce new versions to peers.</summary>
        void Flush(bool force)
        {
            long seq = Index.Seq;
            if (seq == _announcedSeq) return;
            if (!force && DateTime.UtcNow - _lastAnnounce < TimeSpan.FromSeconds(1)) return;
            Index.SaveIfDirty();
            _announcedSeq = seq;
            _lastAnnounce = DateTime.UtcNow;
            IndexAdvanced?.Invoke();
        }

        // ------------------------------------------------------------------ local scan

        struct DiskItem { public string Path; public bool IsDir; public long Size; public long MTime; }

        void Scan()
        {
            if (!Directory.Exists(_root))
            {
                // Shared folder was deleted/moved away. Never interpret that as "delete everything
                // everywhere": recreate it and fetch the full content again from peers.
                Log.Info("Shared folder missing - recreating and resyncing");
                Directory.CreateDirectory(_root);
                Index.Reset();
                _cursors.Clear();
                return;
            }

            var disk = new Dictionary<string, DiskItem>(StringComparer.OrdinalIgnoreCase);
            var unreadable = new List<string>();
            Walk(_root, "", disk, unreadable);

            var now = DateTime.UtcNow;
            foreach (var d in disk.Values)
            {
                var l = Index.Get(d.Path);
                bool changed;
                if (d.IsDir)
                    changed = l == null || l.Deleted || !l.IsDir;
                else
                    changed = l == null || l.Deleted || l.IsDir || l.Size != d.Size || l.MTime != d.MTime;
                if (!changed && l != null && !string.Equals(l.Path, d.Path, StringComparison.Ordinal))
                    changed = true; // case-only rename
                if (!changed) continue;

                string hash = null;
                if (!d.IsDir)
                {
                    // Skip files still being written (recent mtime or locked for writing); retry soon.
                    if (now.Ticks - d.MTime < TimeSpan.FromSeconds(1.5).Ticks || (hash = StableFingerprint(PathUtil.ToFull(_root, d.Path))) == null)
                    {
                        _scanRequested = true;
                        continue;
                    }
                    if (l != null && !l.Deleted && !l.IsDir && l.Hash == hash && l.Size == d.Size && string.Equals(l.Path, d.Path, StringComparison.Ordinal))
                    {
                        // Only the timestamp moved: refresh local metadata, no new version.
                        var same = l.Clone();
                        same.MTime = d.MTime;
                        Index.Put(same);
                        continue;
                    }
                }
                Index.Put(new FileEntry
                {
                    Path = d.Path, IsDir = d.IsDir, Size = d.IsDir ? 0 : d.Size, MTime = d.IsDir ? 0 : d.MTime,
                    Hash = hash, Stamp = HybridClock.Next(), Origin = _myId,
                });
                Notify(d.Path, d.IsDir, false);
            }

            foreach (var l in Index.Snapshot())
            {
                if (l.Deleted || disk.ContainsKey(l.Path)) continue;
                if (unreadable.Any(u => PathUtil.IsUnder(l.Path, u))) continue;
                var t = l.Clone();
                t.Deleted = true;
                t.Size = 0;
                t.Stamp = HybridClock.Next();
                t.Origin = _myId;
                Index.Put(t);
                Notify(t.Path, t.IsDir, true);
            }
        }

        void Walk(string full, string rel, Dictionary<string, DiskItem> acc, List<string> unreadable)
        {
            try
            {
                var di = new DirectoryInfo(full);
                foreach (var fsi in di.EnumerateFileSystemInfos())
                {
                    if (PathUtil.IsIgnored(fsi.Name)) continue;
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue; // junctions/symlinks/cloud placeholders
                    var r = PathUtil.Join(rel, fsi.Name);
                    if (fsi is DirectoryInfo sub)
                    {
                        acc[r] = new DiskItem { Path = r, IsDir = true };
                        Walk(sub.FullName, r, acc, unreadable);
                    }
                    else if (fsi is FileInfo fi)
                    {
                        acc[r] = new DiskItem { Path = r, Size = fi.Length, MTime = fi.LastWriteTimeUtc.Ticks };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("scan " + full, ex);
                unreadable.Add(rel);
            }
        }

        /// <summary>Fingerprint of a file, or null if someone still has it open for writing.</summary>
        static string StableFingerprint(string full)
        {
            try
            {
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                    return FileEntry.Fingerprint(fs);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ pulling from peers

        void Pull()
        {
            foreach (var p in _peers())
            {
                if (!_running) return;
                _cursors.TryGetValue(p.Id, out var c);
                bool sameEpoch = c != null && c.Epoch == p.Epoch;
                if (sameEpoch && c.Seq >= p.Seq) continue;
                long since = sameEpoch ? c.Seq : 0;
                try
                {
                    using (var client = _connect(p))
                    {
                        var s = client.GetStream();
                        Wire.Send(s, new Dictionary<string, object> { ["t"] = "index", ["since"] = since, ["epoch"] = sameEpoch ? c.Epoch : "" });
                        var h = Wire.Receive(s);
                        int n = (int)h.Long("n");
                        var blob = Wire.ReadExact(s, (int)h.Long("len"));
                        var entries = FileIndex.Unpack(blob, n);
                        foreach (var e in entries)
                        {
                            HybridClock.Observe(e.Stamp);
                            Consider(e, p.Id);
                        }
                        _cursors[p.Id] = new Cursor { Epoch = h.Str("epoch"), Seq = h.Long("seq") };
                        if (entries.Count > 0) Log.Info($"Pulled {entries.Count} entries from {p.Name}");
                    }
                }
                catch (Exception ex) { Log.Error("pull from " + p, ex); }
            }
        }

        void Consider(FileEntry e, string peerId)
        {
            if (!PathUtil.IsSafe(e.Path)) return;
            var l = Index.Get(e.Path);
            if (l != null && !e.NewerThan(l)) return;
            if (_pending.TryGetValue(e.Path, out var p))
            {
                if (p.Entry.SameVersion(e)) { p.Sources.Add(peerId); return; }
                if (!e.NewerThan(p.Entry)) return;
            }
            p = new Pending { Entry = e };
            p.Sources.Add(peerId);
            _pending[e.Path] = p;
        }

        // ------------------------------------------------------------------ applying remote changes

        void ProcessPending()
        {
            if (_pending.Count == 0)
            {
                Report(false, "Up to date", 0);
                return;
            }
            var now = DateTime.UtcNow;
            // Deletions deepest-first, then folders shallowest-first, then files.
            var batch = _pending.Values.Where(p => p.NotBefore <= now)
                .OrderBy(p => p.Entry.Deleted ? 0 : p.Entry.IsDir ? 1 : 2)
                .ThenBy(p => p.Entry.Deleted ? -PathUtil.Depth(p.Entry.Path) : PathUtil.Depth(p.Entry.Path))
                .ThenBy(p => p.Entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in batch)
            {
                if (!_running) return;
                bool done;
                try
                {
                    done = Apply(p);
                    if (done) Notify(p.Entry.Path, p.Entry.IsDir, p.Entry.Deleted);
                }
                catch (Exception ex)
                {
                    Log.Error("apply " + p.Entry, ex);
                    done = false;
                }
                if (done) _pending.Remove(p.Entry.Path);
                else
                {
                    p.Attempts++;
                    p.NotBefore = DateTime.UtcNow.AddSeconds(Math.Min(30, 2 * p.Attempts));
                }
                Flush(false);
            }
            if (_pending.Count == 0) Report(false, "Up to date", 0);
            else Report(true, $"Waiting for {_pending.Count} item(s)\u2026", _pending.Count);
        }

        bool Apply(Pending p)
        {
            var e = p.Entry;
            var l = Index.Get(e.Path);
            if (l != null && !e.NewerThan(l)) return true; // superseded meanwhile
            var full = PathUtil.ToFull(_root, e.Path);

            if (e.Deleted)
            {
                if (e.IsDir)
                {
                    if (Directory.Exists(full) && IsEffectivelyEmpty(full))
                    {
                        try { Directory.Delete(full, true); }
                        catch (Exception ex) { Log.Error("rmdir " + e.Path, ex); return false; }
                    }
                    // If it still holds unsynced local files, the next scan re-creates the folder entry.
                }
                else if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    bool matchesIndex = l != null && !l.Deleted && !l.IsDir && fi.Length == l.Size && fi.LastWriteTimeUtc.Ticks == l.MTime;
                    if (matchesIndex && !MoveToTrash(full, fi)) return false; // locked; retry later
                    // Otherwise the local copy has unsynced edits: keep it, the next scan re-shares it.
                }
                Index.Put(e);
                return true;
            }

            if (e.IsDir)
            {
                if (File.Exists(full)) { Log.Info("Name clash (file vs folder), skipping " + e.Path); return true; }
                Directory.CreateDirectory(full);
                Index.Put(e);
                return true;
            }

            // ---- file
            if (Directory.Exists(full)) { Log.Info("Name clash (folder vs file), skipping " + e.Path); return true; }
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                if (fi.Length == e.Size && SafeFingerprint(full) == e.Hash)
                {
                    // Identical content already here (e.g. same file copied to both PCs).
                    var same = e.Clone();
                    same.MTime = fi.LastWriteTimeUtc.Ticks;
                    Index.Put(same);
                    return true;
                }
                bool matchesIndex = l != null && !l.Deleted && !l.IsDir && fi.Length == l.Size && fi.LastWriteTimeUtc.Ticks == l.MTime;
                if (!matchesIndex)
                {
                    // Local edits not yet indexed: index them first; LWW then decides.
                    RequestScan();
                    return false;
                }
            }

            var tmp = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N") + ".wifile-part");
            if (!TakeFromTrash(e, tmp) && !Download(p, tmp)) return false;
            if (SafeFingerprint(tmp) != e.Hash)
            {
                Log.Error("Fingerprint mismatch for " + e.Path + ", discarding");
                TryDelete(tmp);
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                if (File.Exists(full) && !MoveToTrash(full, new FileInfo(full)))
                {
                    TryDelete(tmp);
                    return false;
                }
                File.Move(tmp, full);
                File.SetLastWriteTimeUtc(full, new DateTime(e.MTime, DateTimeKind.Utc));
            }
            catch (Exception ex)
            {
                Log.Error("place " + e.Path, ex);
                TryDelete(tmp);
                return false;
            }
            var placed = new FileInfo(full);
            var ne = e.Clone();
            ne.Size = placed.Length;
            ne.MTime = placed.LastWriteTimeUtc.Ticks;
            Index.Put(ne);
            return true;
        }

        static string SafeFingerprint(string file)
        {
            try { return FileEntry.Fingerprint(file); } catch { return null; }
        }

        bool IsEffectivelyEmpty(string dir)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                    .All(x => Directory.Exists(x) || PathUtil.IsIgnored(Path.GetFileName(x)));
            }
            catch { return false; }
        }

        bool Download(Pending p, string tmp)
        {
            var e = p.Entry;
            var name = Path.GetFileName(e.Path);
            foreach (var id in p.Sources.ToList())
            {
                var peer = _findPeer(id);
                if (peer == null) continue;
                try
                {
                    using (var c = _connect(peer))
                    {
                        var s = c.GetStream();
                        Wire.Send(s, new Dictionary<string, object> { ["t"] = "get", ["path"] = e.Path, ["size"] = e.Size, ["hash"] = e.Hash });
                        var h = Wire.Receive(s);
                        if (!h.Bool("ok"))
                        {
                            Log.Info($"{peer.Name} can't serve {e.Path}: {h.Str("err")}");
                            continue;
                        }
                        long len = h.Long("len");
                        var last = DateTime.MinValue;
                        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            Wire.Copy(s, fs, len, done =>
                            {
                                if ((DateTime.UtcNow - last).TotalMilliseconds < 250) return;
                                last = DateTime.UtcNow;
                                int pct = len == 0 ? 100 : (int)(done * 100 / len);
                                Report(true, $"Receiving {name} \u2014 {pct}% of {PathUtil.FormatSize(len)} from {peer.Name}", _pending.Count);
                            });
                        }
                        Report(true, $"Received {name}", _pending.Count);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"download {e.Path} from {peer}", ex);
                    TryDelete(tmp);
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ serving peers

        public void ServeIndex(NetworkStream s, Dictionary<string, object> h)
        {
            long since = h.Str("epoch") == Index.Epoch ? h.Long("since") : 0;
            // Read seq before collecting so a concurrent Put is re-sent next time rather than lost.
            long seq = Index.Seq;
            string epoch = Index.Epoch;
            var entries = Index.Since(since);
            var blob = FileIndex.Pack(entries);
            Wire.Send(s, new Dictionary<string, object> { ["ok"] = true, ["epoch"] = epoch, ["seq"] = seq, ["n"] = entries.Count, ["len"] = blob.Length });
            s.Write(blob, 0, blob.Length);
            s.Flush();
        }

        public void ServeFile(NetworkStream s, Dictionary<string, object> h)
        {
            var path = h.Str("path");
            if (!PathUtil.IsSafe(path)) { Wire.Send(s, Wire.Error("bad path")); return; }
            var l = Index.Get(path);
            long size = h.Long("size");
            if (l == null || l.Deleted || l.IsDir || l.Size != size || l.Hash != h.Str("hash"))
            {
                Wire.Send(s, Wire.Error("changed"));
                return;
            }
            var full = PathUtil.ToFull(_root, path);
            FileStream fs;
            try { fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
            catch (Exception ex) { Wire.Send(s, Wire.Error(ex.Message)); return; }
            using (fs)
            {
                if (fs.Length != size || File.GetLastWriteTimeUtc(full).Ticks != l.MTime)
                {
                    Wire.Send(s, Wire.Error("changed"));
                    return;
                }
                Wire.Send(s, new Dictionary<string, object> { ["ok"] = true, ["len"] = size });
                Wire.Copy(fs, s, size);
            }
        }

        // ------------------------------------------------------------------ trash (move detection + safety net)

        /// <summary>Files removed/overwritten by remote changes are parked here for a few minutes, so a
        /// remote move/rename (= delete + create) reuses the local bytes instead of re-downloading,
        /// then go to the Windows Recycle Bin.</summary>
        bool MoveToTrash(string full, FileInfo fi)
        {
            try
            {
                var dir = Path.Combine(_trashDir, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, fi.Name);
                long size = fi.Length;
                File.Move(full, dest);
                _trash.Add(new TrashItem { Path = dest, Size = size, Hash = SafeFingerprint(dest), At = DateTime.UtcNow });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("trash " + full, ex);
                return false;
            }
        }

        bool TakeFromTrash(FileEntry e, string dest)
        {
            var t = _trash.FirstOrDefault(x => x.Size == e.Size && x.Hash == e.Hash && File.Exists(x.Path));
            if (t == null) return false;
            try
            {
                File.Move(t.Path, dest);
                _trash.Remove(t);
                TryDeleteDir(Path.GetDirectoryName(t.Path));
                return true;
            }
            catch { return false; }
        }

        void PurgeTrash()
        {
            _lastPurge = DateTime.UtcNow;
            foreach (var t in _trash.Where(x => DateTime.UtcNow - x.At > TrashKeep).ToList())
            {
                _trash.Remove(t);
                Recycle(Path.GetDirectoryName(t.Path));
            }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        /// <summary>Send to the Recycle Bin silently; fall back to deleting.</summary>
        static void Recycle(string path)
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var op = new SHFILEOPSTRUCT
                    {
                        wFunc = 3, // FO_DELETE
                        pFrom = path + "\0\0",
                        fFlags = 0x0454, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                    };
                    if (SHFileOperation(ref op) == 0 && !Directory.Exists(path) && !File.Exists(path)) return;
                }
            }
            catch { }
            TryDeleteDir(path);
            TryDelete(path);
        }

        // ------------------------------------------------------------------

        void Notify(string rel, bool isDir, bool removed)
        {
            try { ItemChanged?.Invoke(PathUtil.ToFull(_root, rel), isDir, removed); }
            catch (Exception ex) { Log.Error("notify", ex); }
        }

        void Report(bool busy, string text, int pending)
        {
            if (text == _lastStatus) return;
            _lastStatus = text;
            CurrentStatus = new SyncStatus { Busy = busy, Text = text, Pending = pending };
            StatusChanged?.Invoke(CurrentStatus);
        }

        public void Dispose()
        {
            _running = false;
            _wake.Set();
            try { _watcher?.Dispose(); } catch { }
            _thread?.Join(5000);
            Index.SaveIfDirty();
        }
    }
}
