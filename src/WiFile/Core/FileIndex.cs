using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WiFile.Core
{
    /// <summary>
    /// One versioned item of the shared folder. Deleted items are kept as tombstones so a
    /// deletion propagates instead of the file being "re-shared" by a peer that still has it.
    /// Entries are treated as immutable once stored in the index.
    /// </summary>
    public sealed class FileEntry
    {
        public string Path;     // relative, '/' separated
        public bool IsDir;
        public bool Deleted;
        public long Size;
        public long MTime;      // UTC ticks of last write
        public string Hash;     // content fingerprint (files only)
        public long Stamp;      // hybrid clock of the change
        public string Origin;   // device id that made the change
        public long Seq;        // local sequence number (for delta sync), not replicated

        /// <summary>Total order used for last-writer-wins.</summary>
        public bool NewerThan(FileEntry o) =>
            o == null || Stamp > o.Stamp || (Stamp == o.Stamp && string.CompareOrdinal(Origin, o.Origin) > 0);

        public bool SameVersion(FileEntry o) => o != null && Stamp == o.Stamp && Origin == o.Origin;

        public FileEntry Clone() => (FileEntry)MemberwiseClone();

        public void Write(BinaryWriter w)
        {
            w.Write(Path);
            w.Write((byte)((IsDir ? 1 : 0) | (Deleted ? 2 : 0)));
            w.Write(Size);
            w.Write(MTime);
            w.Write(Hash ?? "");
            w.Write(Stamp);
            w.Write(Origin ?? "");
            w.Write(Seq);
        }

        public static FileEntry Read(BinaryReader r)
        {
            var e = new FileEntry { Path = r.ReadString() };
            byte f = r.ReadByte();
            e.IsDir = (f & 1) != 0;
            e.Deleted = (f & 2) != 0;
            e.Size = r.ReadInt64();
            e.MTime = r.ReadInt64();
            e.Hash = r.ReadString();
            e.Stamp = r.ReadInt64();
            e.Origin = r.ReadString();
            e.Seq = r.ReadInt64();
            return e;
        }

        /// <summary>Fast fingerprint: MD5 over size + first 64 KB + last 64 KB. Cheap even for huge files,
        /// and distinguishes contents that happen to share size and timestamp.</summary>
        public static string Fingerprint(Stream fs)
        {
            const int Chunk = 64 * 1024;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                long len = fs.Length;
                var buf = new byte[Chunk];
                md5.TransformBlock(BitConverter.GetBytes(len), 0, 8, null, 0);
                fs.Position = 0;
                int n = ReadFull(fs, buf, (int)Math.Min(Chunk, len));
                md5.TransformBlock(buf, 0, n, null, 0);
                if (len > Chunk)
                {
                    fs.Position = Math.Max(Chunk, len - Chunk);
                    n = ReadFull(fs, buf, (int)(len - fs.Position));
                    md5.TransformBlock(buf, 0, n, null, 0);
                }
                md5.TransformFinalBlock(new byte[0], 0, 0);
                return Convert.ToBase64String(md5.Hash).TrimEnd('=');
            }
        }

        public static string Fingerprint(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                return Fingerprint(fs);
        }

        static int ReadFull(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) break;
                off += n;
            }
            return off;
        }

        public override string ToString() => $"{(Deleted ? "DEL " : "")}{(IsDir ? "dir " : "")}{Path} ({Size}b @{Stamp} by {Origin})";
    }

    /// <summary>Thread-safe index of the shared folder with a monotonically increasing sequence number.</summary>
    public sealed class FileIndex
    {
        const int Magic = 0x32494657; // "WFI2"
        readonly Dictionary<string, FileEntry> _map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        readonly object _gate = new object();
        readonly string _file;
        bool _dirty;

        /// <summary>Identifies this index incarnation; peers restart delta sync when it changes.</summary>
        public string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
        public long Seq { get { lock (_gate) return _seq; } }
        long _seq;

        public FileIndex(string file) { _file = file; }

        public FileEntry Get(string path)
        {
            lock (_gate) return _map.TryGetValue(path, out var e) ? e : null;
        }

        public void Put(FileEntry e)
        {
            lock (_gate)
            {
                e.Seq = ++_seq;
                _map[e.Path] = e;
                _dirty = true;
            }
        }

        public List<FileEntry> Snapshot()
        {
            lock (_gate) return _map.Values.ToList();
        }

        public List<FileEntry> Since(long seq)
        {
            lock (_gate) return _map.Values.Where(e => e.Seq > seq).ToList();
        }

        /// <summary>Forget everything (used when the shared folder vanished) so peers re-send all files.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _map.Clear();
                _seq = 0;
                Epoch = Guid.NewGuid().ToString("N");
                _dirty = true;
            }
        }

        public void Load()
        {
            if (!File.Exists(_file)) return;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(_file), Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) throw new InvalidDataException("bad magic");
                    var epoch = r.ReadString();
                    long seq = r.ReadInt64();
                    int n = r.ReadInt32();
                    var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < n; i++)
                    {
                        var e = FileEntry.Read(r);
                        map[e.Path] = e;
                    }
                    lock (_gate)
                    {
                        _map.Clear();
                        foreach (var kv in map) _map[kv.Key] = kv.Value;
                        Epoch = epoch;
                        _seq = seq;
                    }
                }
                Log.Info($"Index loaded: {Snapshot().Count} entries, seq {Seq}");
            }
            catch (Exception ex)
            {
                Log.Error("Index load failed, starting fresh", ex);
                Reset();
            }
        }

        public void SaveIfDirty()
        {
            List<FileEntry> entries;
            string epoch;
            long seq;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                entries = _map.Values.ToList();
                epoch = Epoch;
                seq = _seq;
            }
            try
            {
                var tmp = _file + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(epoch);
                    w.Write(seq);
                    w.Write(entries.Count);
                    foreach (var e in entries) e.Write(w);
                }
                if (File.Exists(_file)) File.Replace(tmp, _file, null);
                else File.Move(tmp, _file);
            }
            catch (Exception ex)
            {
                Log.Error("Index save failed", ex);
                lock (_gate) _dirty = true;
            }
        }

        public static byte[] Pack(List<FileEntry> entries)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    foreach (var e in entries) e.Write(w);
                return ms.ToArray();
            }
        }

        public static List<FileEntry> Unpack(byte[] data, int count)
        {
            var list = new List<FileEntry>(count);
            using (var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
                for (int i = 0; i < count; i++) list.Add(FileEntry.Read(r));
            return list;
        }
    }
}
