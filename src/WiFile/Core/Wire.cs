using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WiFile.Core
{
    /// <summary>
    /// Peer-to-peer TCP framing. Every message is: int32 length + UTF-8 JSON header,
    /// optionally followed by exactly header["len"] raw bytes (file content, index blob).
    /// One request/response per connection keeps the protocol stateless and robust.
    /// </summary>
    public static class Wire
    {
        const int MaxHeader = 16 * 1024 * 1024;
        const int BufferSize = 256 * 1024;

        public static void Send(Stream s, Dictionary<string, object> header)
        {
            var body = Encoding.UTF8.GetBytes(Json.Serialize(header));
            var frame = new byte[4 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(frame, 0);
            body.CopyTo(frame, 4);
            s.Write(frame, 0, frame.Length);
            s.Flush();
        }

        public static Dictionary<string, object> Receive(Stream s)
        {
            int len = BitConverter.ToInt32(ReadExact(s, 4), 0);
            if (len <= 0 || len > MaxHeader) throw new IOException("Invalid frame length " + len);
            return Json.Parse(Encoding.UTF8.GetString(ReadExact(s, len)));
        }

        public static byte[] ReadExact(Stream s, int count)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException("Connection closed");
                off += n;
            }
            return buf;
        }

        /// <summary>Copies exactly <paramref name="count"/> bytes, reporting progress in bytes.</summary>
        public static void Copy(Stream src, Stream dst, long count, Action<long> progress = null)
        {
            var buf = new byte[BufferSize];
            long done = 0;
            while (done < count)
            {
                int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count - done));
                if (n <= 0) throw new EndOfStreamException("Connection closed during transfer");
                dst.Write(buf, 0, n);
                done += n;
                progress?.Invoke(done);
            }
            dst.Flush();
        }

        public static TcpClient Connect(IPEndPoint ep, int timeoutMs = 4000)
        {
            var c = new TcpClient(ep.AddressFamily);
            try
            {
                var ar = c.BeginConnect(ep.Address, ep.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException("Connect timeout to " + ep);
                c.EndConnect(ar);
                Tune(c);
                return c;
            }
            catch
            {
                c.Close();
                throw;
            }
        }

        public static void Tune(TcpClient c)
        {
            c.NoDelay = true;
            c.ReceiveTimeout = 30000;
            c.SendTimeout = 30000;
            c.ReceiveBufferSize = 1 << 20;
            c.SendBufferSize = 1 << 20;
        }

        public static Dictionary<string, object> Error(string msg) =>
            new Dictionary<string, object> { ["ok"] = false, ["err"] = msg };
    }
}
