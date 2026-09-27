using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace WiFile.Core
{
    /// <summary>Tiny rolling file logger.</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        static string _file;
        public static bool ToConsole;

        public static void Init(string dir)
        {
            _file = Path.Combine(dir, "wifile.log");
            try
            {
                if (File.Exists(_file) && new FileInfo(_file).Length > 2_000_000)
                {
                    File.Copy(_file, _file + ".old", true);
                    File.Delete(_file);
                }
            }
            catch { }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERR ", ex == null ? msg : msg + ": " + ex.GetType().Name + ": " + ex.Message);

        static void Write(string level, string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}";
            Debug.WriteLine(line);
            if (ToConsole) Console.WriteLine(line);
            if (_file == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(_file, line + Environment.NewLine); } catch { }
            }
        }
    }

    public static class Json
    {
        static JavaScriptSerializer Make() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        public static string Serialize(object o) => Make().Serialize(o);
        public static T Deserialize<T>(string s) => Make().Deserialize<T>(s);
        public static Dictionary<string, object> Parse(string s) => Make().Deserialize<Dictionary<string, object>>(s);

        public static string Str(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : null;

        public static long Long(this Dictionary<string, object> d, string key, long def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            try { return Convert.ToInt64(v); } catch { return def; }
        }

        public static bool Bool(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b && b;
    }

    /// <summary>Hybrid logical clock: wall-clock milliseconds that never go backwards and
    /// always move past any timestamp seen from a peer, so "newer" is well defined.</summary>
    public static class HybridClock
    {
        static readonly object Gate = new object();
        static long _last;

        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static long Next()
        {
            lock (Gate) { _last = Math.Max(Now, _last + 1); return _last; }
        }

        public static void Observe(long stamp)
        {
            lock (Gate)
            {
                // Ignore absurd future stamps (> 1 day ahead) from a peer with a broken clock.
                if (stamp > _last && stamp < Now + 86_400_000L) _last = stamp;
            }
        }
    }

    public static class PathUtil
    {
        static readonly char[] Invalid = Path.GetInvalidFileNameChars();
        static readonly HashSet<string> IgnoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "desktop.ini", "Thumbs.db", "ehthumbs.db", ".DS_Store", "$RECYCLE.BIN", "System Volume Information"
        };

        /// <summary>Relative paths always use '/' on the wire and in the index.</summary>
        public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

        public static bool IsSafe(string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Length > 1024) return false;
            foreach (var seg in rel.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") return false;
                if (seg.IndexOfAny(Invalid) >= 0) return false;
                if (seg.EndsWith(" ") || seg.EndsWith(".")) return false;
            }
            return true;
        }

        public static string ToFull(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

        public static int Depth(string rel) => rel.Count(c => c == '/');

        public static string Parent(string rel)
        {
            int i = rel.LastIndexOf('/');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        public static bool IsUnder(string rel, string dir) =>
            rel.Length > dir.Length && rel[dir.Length] == '/' && rel.StartsWith(dir, StringComparison.OrdinalIgnoreCase);

        /// <summary>Editor lock files, OS metadata and our own partial downloads never sync.</summary>
        public static bool IsIgnored(string name)
        {
            if (IgnoredNames.Contains(name)) return true;
            if (name.StartsWith("~$") || name.StartsWith(".~lock")) return true;
            if (name.StartsWith("~") && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".wifile-part", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string SafeFileName(string name)
        {
            name = Path.GetFileName(name ?? "") ?? "";
            foreach (var c in Invalid) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return name.Length == 0 ? "file" : name;
        }

        /// <summary>"photo.jpg" -> "photo (1).jpg" if taken.</summary>
        public static string UniquePath(string dir, string name)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int i = 1; ; i++)
            {
                path = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
        }
    }
}
