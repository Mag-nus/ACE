using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Bench
{
    public static class SocketBench
    {
        const int Datagram = 120;

        // K client sockets bound to distinct loopback ports send to the server socket, round robin, zero-alloc API.
        static Socket[] MakeClients(int k)
        {
            var arr = new Socket[k];
            for (int i = 0; i < k; i++) { arr[i] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); arr[i].Bind(new IPEndPoint(IPAddress.Loopback, 0)); }
            return arr;
        }

        static void Blast(Socket[] clients, SocketAddress server, int n, ref int received)
        {
            var payload = new byte[Datagram];
            for (int i = 0; i < n; i++)
            {
                clients[i % clients.Length].SendTo(payload, SocketFlags.None, server);
                // keep the receiver from being flooded into drops: allow at most ~512 in flight
                if ((i & 63) == 63) { var spin = new SpinWait(); while (i - Volatile.Read(ref received) > 512) spin.SpinOnce(); }
            }
        }

        static Socket MakeServer()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.ReceiveBufferSize = 4 << 20;
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return s;
        }

        // ---------- R1: the current ConnectionListener shape (APM + two IPEndPoint per datagram) ----------
        sealed class ApmListener
        {
            public Socket Socket; public readonly byte[] buffer = new byte[1024]; public int Received; public IPAddress host = IPAddress.Any; public volatile bool stop;
            public void Listen()
            {
                try { EndPoint clientEndPoint = new IPEndPoint(host, 0); Socket.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref clientEndPoint, OnDataReceive, Socket); }
                catch (SocketException) { Listen(); }
                catch (ObjectDisposedException) { }
            }
            void OnDataReceive(IAsyncResult result)
            {
                EndPoint clientEndPoint = null;
                try
                {
                    clientEndPoint = new IPEndPoint(host, 0);
                    int dataSize = Socket.EndReceiveFrom(result, ref clientEndPoint);
                    IPEndPoint ipEndpoint = (IPEndPoint)clientEndPoint;
                    Interlocked.Increment(ref Received);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
                if (stop) return;
                if (result.CompletedSynchronously) Task.Run(() => Listen()); else Listen();
            }
        }

        public static void Run()
        {
            const int N = 200_000;
            var clients = MakeClients(64);

            H.Header("SENDER cost (64 distinct destination endpoints, from ONE server socket) - per datagram");
            {
                var server = MakeServer();
                var dests = new IPEndPoint[64]; var destAddrs = new SocketAddress[64];
                for (int i = 0; i < 64; i++) { dests[i] = (IPEndPoint)clients[i].LocalEndPoint; destAddrs[i] = dests[i].Serialize(); }
                var buf = new byte[484];
                H.Measure("SendTo(byte[], size, flags, EndPoint) - current NetworkSession.SendPacketRaw", 100_000, n => { for (int i = 0; i < n; i++) server.SendTo(buf, 120, SocketFlags.None, dests[i & 63]); }, runs: 3);
                H.Measure("SendTo(byte[], size, flags, EndPoint) same endpoint every time", 100_000, n => { for (int i = 0; i < n; i++) server.SendTo(buf, 120, SocketFlags.None, dests[0]); }, runs: 3);
                H.Measure("SendTo(ReadOnlySpan, flags, SocketAddress cached per session)", 100_000, n => { for (int i = 0; i < n; i++) server.SendTo(buf.AsSpan(0, 120), SocketFlags.None, destAddrs[i & 63]); }, runs: 3);
                // drain client sockets
                foreach (var c in clients) { c.Blocking = false; var tmp = new byte[2048]; try { while (true) c.Receive(tmp); } catch (SocketException) { } c.Blocking = true; }
                server.Dispose();
            }

            H.Header("RECEIVER cost per datagram (allocations: all threads, receiver+sender; sender is zero-alloc)");
            // sanity: sender alone allocates nothing
            {
                var server = MakeServer(); var sa = server.LocalEndPoint.Serialize(); int dummy = int.MaxValue / 2;
                long a0 = GC.GetTotalAllocatedBytes(true);
                for (int i = 0; i < 10000; i++) clients[i & 63].SendTo(new ReadOnlySpan<byte>(new byte[0], 0, 0), SocketFlags.None, sa);
                // above allocates the empty array; redo with a static array
                var p = new byte[Datagram];
                a0 = GC.GetTotalAllocatedBytes(true);
                for (int i = 0; i < 10000; i++) clients[i & 63].SendTo(p, SocketFlags.None, sa);
                Console.WriteLine($"sender alone: {(GC.GetTotalAllocatedBytes(true) - a0) / 10000.0:F1} B/datagram");
                server.Dispose();
            }

            for (int rep = 0; rep < 2; rep++)
            {
                RunReceiver("R1 APM BeginReceiveFrom/EndReceiveFrom (current)", clients, N, (server, stopFlag) =>
                {
                    var l = new ApmListener { Socket = server };
                    l.Listen();
                    return (() => l.Received, () => { l.stop = true; });
                });

                RunReceiver("R2 await ReceiveFromAsync(Memory, flags, SocketAddress) loop", clients, N, (server, stopFlag) =>
                {
                    int received = 0; var cts = new CancellationTokenSource();
                    var buffer = GC.AllocateUninitializedArray<byte>(1024, pinned: true);
                    var addr = new SocketAddress(AddressFamily.InterNetwork);
                    _ = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try { int n = await server.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, addr, cts.Token).ConfigureAwait(false); Interlocked.Increment(ref received); }
                            catch (OperationCanceledException) { return; }
                            catch (ObjectDisposedException) { return; }
                            catch (SocketException) { }
                        }
                    });
                    return (() => Volatile.Read(ref received), () => cts.Cancel());
                });

                RunReceiver("R3 dedicated thread, blocking ReceiveFrom(Span, flags, SocketAddress)", clients, N, (server, stopFlag) =>
                {
                    int received = 0; bool stop = false;
                    var t = new Thread(() =>
                    {
                        var buffer = new byte[1024]; var addr = new SocketAddress(AddressFamily.InterNetwork);
                        while (!Volatile.Read(ref stop))
                        {
                            try { int n = server.ReceiveFrom(buffer, SocketFlags.None, addr); Interlocked.Increment(ref received); }
                            catch (SocketException) { }
                            catch (ObjectDisposedException) { return; }
                        }
                    }) { IsBackground = true };
                    t.Start();
                    return (() => Volatile.Read(ref received), () => Volatile.Write(ref stop, true));
                });

                RunReceiver("R4 R2 + materialise IPEndPoint per datagram (design doc 5b option 1)", clients, N, (server, stopFlag) =>
                {
                    int received = 0; var cts = new CancellationTokenSource();
                    var buffer = GC.AllocateUninitializedArray<byte>(1024, pinned: true);
                    var addr = new SocketAddress(AddressFamily.InterNetwork);
                    var template = new IPEndPoint(IPAddress.Any, 0);
                    _ = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try { int n = await server.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, addr, cts.Token).ConfigureAwait(false); var ep = (IPEndPoint)template.Create(addr); Interlocked.Increment(ref received); }
                            catch (OperationCanceledException) { return; }
                            catch (ObjectDisposedException) { return; }
                            catch (SocketException) { }
                        }
                    });
                    return (() => Volatile.Read(ref received), () => cts.Cancel());
                });
            }

            H.Header("Endpoint validation: per-datagram cost of the check NetworkManager.ProcessPacket does");
            {
                var a = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 9000); var b = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 9000);
                var sa = a.Serialize(); var sb = b.Serialize();
                H.Measure("IPEndPoint.Equals(IPEndPoint)", 10_000_000, n => { int c = 0; for (int i = 0; i < n; i++) if (a.Equals(b)) c++; GC.KeepAlive(c); });
                H.Measure("SocketAddress.Equals(SocketAddress)", 10_000_000, n => { int c = 0; for (int i = 0; i < n; i++) if (sa.Equals(sb)) c++; GC.KeepAlive(c); });
                H.Measure("SocketAddress.Buffer.Span.SequenceEqual", 10_000_000, n => { int c = 0; for (int i = 0; i < n; i++) if (sa.Buffer.Span.Slice(0, sa.Size).SequenceEqual(sb.Buffer.Span.Slice(0, sb.Size))) c++; GC.KeepAlive(c); });
            }
        }

        static void RunReceiver(string name, Socket[] clients, int n, Func<Socket, object, (Func<int> count, Action stop)> start)
        {
            var server = MakeServer();
            var sa = server.LocalEndPoint.Serialize();
            var (count, stop) = start(server, null);
            // warmup (no backpressure needed for 5000)
            { var pl = new byte[Datagram]; for (int i = 0; i < 5000; i++) { clients[i % clients.Length].SendTo(pl, SocketFlags.None, sa); if ((i & 255) == 255) Thread.Sleep(1); } }
            var swWait = Stopwatch.StartNew();
            while (count() < 4900 && swWait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            int baseCount = count();
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            long a0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            // sender with backpressure on the receive count
            var payload = new byte[Datagram];
            for (int i = 0; i < n; i++)
            {
                clients[i % clients.Length].SendTo(payload, SocketFlags.None, sa);
                if ((i & 63) == 63) { var spin = new SpinWait(); var deadline = sw.ElapsedMilliseconds + 200; while (i - (count() - baseCount) > 512 && sw.ElapsedMilliseconds < deadline) spin.SpinOnce(); }
            }
            var deadline2 = sw.ElapsedMilliseconds + 2000;
            while (count() - baseCount < n && sw.ElapsedMilliseconds < deadline2) Thread.Sleep(1);
            sw.Stop();
            int got = count() - baseCount;
            long a1 = GC.GetTotalAllocatedBytes(true);
            proc.Refresh();
            var cpu = proc.TotalProcessorTime - cpu0;
            stop();
            server.Dispose();
            Console.WriteLine($"{name,-72} recv {got,7}/{n}  {(double)(a1 - a0) / got,7:F1} B/dgram  {cpu.TotalMilliseconds * 1e6 / got,7:F0} ns CPU/dgram (process, incl. sender)  wall {sw.Elapsed.TotalMilliseconds * 1e6 / got,6:F0} ns");
        }
    }
}
