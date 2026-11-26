using System.Diagnostics; using System.Net; using System.Net.Sockets;
// Process CPU per Socket.SendTo on a UDP socket, depending on how the SAME socket is being received on.
// (Linux: a socket with a pending async receive is registered with epoll, and every send raises EPOLLOUT.)
static class P
{
    static Socket Make() { var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return s; }
    static void Measure(string name, Socket s, EndPoint dest)
    {
        var buf = new byte[24];
        for (int w = 0; w < 20000; w++) s.SendTo(buf, 24, SocketFlags.None, dest);
        Thread.Sleep(300);
        // tight loop
        var p = Process.GetCurrentProcess(); var cpu0 = p.TotalProcessorTime; var sw = Stopwatch.StartNew(); int n = 0;
        while (sw.ElapsedMilliseconds < 1500) { for (int i = 0; i < 64; i++) s.SendTo(buf, 24, SocketFlags.None, dest); n += 64; }
        sw.Stop(); p.Refresh(); var cpu = (p.TotalProcessorTime - cpu0).TotalMilliseconds;
        // main-loop shape: 4 sends then Sleep(1)
        p.Refresh(); cpu0 = p.TotalProcessorTime; var sw2 = Stopwatch.StartNew(); int n2 = 0;
        while (sw2.ElapsedMilliseconds < 2000) { for (int i = 0; i < 4; i++) s.SendTo(buf, 24, SocketFlags.None, dest); n2 += 4; Thread.Sleep(1); }
        sw2.Stop(); p.Refresh(); var cpu2 = (p.TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine($"{name,-58} tight: {sw.Elapsed.TotalMilliseconds * 1e6 / n,6:F0} ns wall, {cpu * 1e6 / n,6:F0} ns process CPU per send | sleep-loop: {cpu2 / sw2.Elapsed.TotalSeconds,6:F1} ms CPU per second at {n2 / sw2.Elapsed.TotalSeconds,5:F0} sends/s");
    }
    static void Main()
    {
        var sink = Make(); sink.ReceiveBufferSize = 4096; var dest = sink.LocalEndPoint;
        // 1. plain
        Measure("plain socket, nothing receiving", Make(), dest);
        // 2. dedicated thread blocked in the synchronous ReceiveFrom(Span, SocketFlags, SocketAddress)
        var b = Make(); var t = new Thread(() => { var rb = new byte[1024]; var sa = new SocketAddress(AddressFamily.InterNetwork); try { while (true) b.ReceiveFrom(rb, SocketFlags.None, sa); } catch { } }) { IsBackground = true }; t.Start(); Thread.Sleep(100);
        Measure("blocking ReceiveFrom on a dedicated thread (NT.2 design)", b, dest);
        // 3. APM receive pending (what ConnectionListener does)
        var a = Make(); EndPoint ep = new IPEndPoint(IPAddress.Loopback, 0); var ab = new byte[1024]; a.BeginReceiveFrom(ab, 0, ab.Length, SocketFlags.None, ref ep, _ => { }, a); Thread.Sleep(100);
        Measure("BeginReceiveFrom pending (current ConnectionListener)", a, dest);
        // 4. await ReceiveFromAsync pending
        var c = Make(); var cb = new byte[1024]; var csa = new SocketAddress(AddressFamily.InterNetwork); _ = c.ReceiveFromAsync(cb.AsMemory(), SocketFlags.None, csa).AsTask(); Thread.Sleep(100);
        Measure("ReceiveFromAsync(Memory, SocketAddress) pending", c, dest);
    }
}
