using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace WiFile.Core
{
    /// <summary>Accepts one request per TCP connection and hands it to the node's dispatcher.</summary>
    public sealed class PeerServer : IDisposable
    {
        readonly Action<NetworkStream, Dictionary<string, object>> _handler;
        TcpListener _listener;
        volatile bool _running;

        public int Port { get; private set; }

        public PeerServer(Action<NetworkStream, Dictionary<string, object>> handler) { _handler = handler; }

        public void Start(int preferredPort)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, preferredPort);
                _listener.Start();
            }
            catch (SocketException)
            {
                // Port taken (e.g. a second instance) - any free port works, it's advertised in beacons.
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
            }
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "WiFile-Server" }.Start();
            Log.Info("Listening on TCP " + Port);
        }

        void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var c = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => Serve(c));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (!_running) break; }
                catch (Exception ex) { Log.Error("accept", ex); }
            }
        }

        void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    Wire.Tune(c);
                    var s = c.GetStream();
                    var h = Wire.Receive(s);
                    _handler(s, h);
                }
            }
            catch (Exception ex) { Log.Error("serve", ex); }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }
    }
}
