using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using WiFile.Core;

// End-to-end test: three real nodes in one process talking over loopback TCP/UDP.
static class Program
{
    static int _failures;
    static string _base;

    static int Main(string[] args)
    {
        Log.ToConsole = args.Contains("-v");
        _base = Path.Combine(Path.GetTempPath(), "wifile-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Console.WriteLine("Test dir: " + _base);

        int[] udp = { 47001, 47002, 47003 };
        var nodes = new WiFileNode[3];
        for (int i = 0; i < 3; i++) nodes[i] = MakeNode(i, udp);
        foreach (var n in nodes) n.Start();
        var (a, b, c) = (nodes[0], nodes[1], nodes[2]);

        Check("discovery", () => nodes.All(n => n.Discovery.Peers.Count == 2), 10);

        // 1. create file + nested folder
        File.WriteAllText(P(a, "hello.txt"), "hello world");
        Directory.CreateDirectory(P(a, @"Photos\2024"));
        File.WriteAllText(P(a, @"Photos\2024\pic.jpg"), "fake jpeg");
        Check("create propagates", () => Same(nodes, "hello.txt") && Same(nodes, @"Photos\2024\pic.jpg"));

        // 2. modify on B
        Thread.Sleep(300);
        File.WriteAllText(P(b, "hello.txt"), "hello from B, edited");
        Check("edit propagates", () => nodes.All(n => Read(n, "hello.txt") == "hello from B, edited"));

        // 3. delete on C
        File.Delete(P(c, "hello.txt"));
        Check("delete propagates", () => nodes.All(n => !File.Exists(P(n, "hello.txt"))));

        // 4. folder rename on A (move = delete + create; bytes reused from trash)
        Directory.Move(P(a, "Photos"), P(a, "Pictures"));
        Check("folder move propagates", () => nodes.All(n => !Directory.Exists(P(n, "Photos")) && File.Exists(P(n, @"Pictures\2024\pic.jpg"))));

        // 5. large file
        var big = new byte[60 * 1024 * 1024];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(P(b, "big.bin"), big);
        var sw = Stopwatch.StartNew();
        Check("60 MB file propagates", () => Same(nodes, "big.bin"), 60);
        Console.WriteLine($"      (60 MB replicated to 2 peers in {sw.Elapsed.TotalSeconds:0.0}s)");

        // 6. empty folder + delete folder tree
        Directory.CreateDirectory(P(c, @"Empty\Nested"));
        Check("empty folders propagate", () => nodes.All(n => Directory.Exists(P(n, @"Empty\Nested"))));
        Directory.Delete(P(b, "Pictures"), true);
        Check("folder tree delete propagates", () => nodes.All(n => !Directory.Exists(P(n, "Pictures"))));

        // 7. concurrent conflicting edits converge to a single winner
        File.WriteAllText(P(a, "conflict.txt"), "version A");
        File.WriteAllText(P(b, "conflict.txt"), "version B");
        Check("conflict converges", () => Same(nodes, "conflict.txt"), 30);
        foreach (var n in nodes)
            Console.WriteLine($"      {n.Name}: '{Read(n, "conflict.txt")}' mtime={File.GetLastWriteTimeUtc(P(n, "conflict.txt")).Ticks} idx={n.Sync.Index.Get("conflict.txt")}");

        // 8. chat text + attachment
        var received = new List<ChatMessage>();
        b.Chat.MessageReceived += m => { lock (received) received.Add(m); };
        c.Chat.MessageReceived += m => { lock (received) received.Add(m); };
        a.Chat.SendText("hi all https://example.com", null);
        var att = Path.Combine(_base, "song.mp3");
        File.WriteAllBytes(att, big.Take(3_000_000).ToArray());
        a.Chat.SendFile(att, b.Id); // direct message to B only
        Check("chat delivered", () =>
        {
            lock (received)
                return received.Count(m => m.Text == "hi all https://example.com") == 2
                    && received.Count(m => m.FileName == "song.mp3") == 1
                    && received.Where(m => m.FileName == "song.mp3").All(m => File.Exists(m.LocalPath) && new FileInfo(m.LocalPath).Length == 3_000_000);
        });

        // 9. offline changes: stop C, change things on A, restart C
        c.Dispose();
        Thread.Sleep(500);
        File.Delete(P(a, "big.bin"));
        File.WriteAllText(P(a, "while-offline.txt"), "you missed me");
        var c2 = MakeNode(2, udp);
        c2.Start();
        nodes[2] = c2;
        Check("offline node catches up", () => !File.Exists(P(c2, "big.bin")) && Read(c2, "while-offline.txt") == "you missed me", 30);

        // 10. file created on restarted node while others keep running
        File.WriteAllText(P(c2, "from-c.txt"), "back online");
        Check("restarted node shares again", () => Same(nodes, "from-c.txt"));

        foreach (var n in nodes) n.Dispose();
        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} TEST(S) FAILED");
        try { Directory.Delete(_base, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    static WiFileNode MakeNode(int i, int[] udp)
    {
        var dir = Path.Combine(_base, "node" + i);
        var cfg = new NodeConfig
        {
            SharedDir = Path.Combine(dir, "shared"),
            DataDir = Path.Combine(dir, "data"),
            ReceiveDir = Path.Combine(dir, "received"),
            DiscoveryPort = udp[i],
            TcpPort = 0,
            UseBroadcast = false,
            ExtraBeaconTargets = udp.Where((p, j) => j != i).Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToList(),
        };
        var n = new WiFileNode(cfg);
        n.Rename("Node" + (char)('A' + i));
        return n;
    }

    static string P(WiFileNode n, string rel) => Path.Combine(n.Config.SharedDir, rel);

    static string Read(WiFileNode n, string rel)
    {
        try { return File.ReadAllText(P(n, rel)); } catch { return null; }
    }

    static bool Same(WiFileNode[] nodes, string rel)
    {
        try
        {
            var hashes = nodes.Select(n =>
            {
                using (var md5 = MD5.Create())
                using (var fs = new FileStream(P(n, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return Convert.ToBase64String(md5.ComputeHash(fs));
            }).Distinct().Count();
            return hashes == 1;
        }
        catch { return false; }
    }

    static void Check(string name, Func<bool> cond, int timeoutSec = 20)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            if (cond())
            {
                Console.WriteLine($"PASS  {name}  ({sw.Elapsed.TotalSeconds:0.0}s)");
                return;
            }
            Thread.Sleep(200);
        }
        _failures++;
        Console.WriteLine($"FAIL  {name}  (timeout {timeoutSec}s)");
    }
}
