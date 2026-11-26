using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity;
using ACE.Server.Managers;

// Real LandblockManager.GetLandblock(id, false, false) - the call behind every physics LScape.get_landcell on the server -
// with the landblock already loaded (the steady-state path). Measures throughput with 1..N threads.
static class P
{
    static void Main()
    {
        var arr = (ACE.Server.Entity.Landblock[,])typeof(LandblockManager).GetField("landblocks", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        // 16 loaded landblocks; uninitialised instances are enough: the fast path only returns the reference
        var ids = new LandblockId[16];
        for (int i = 0; i < 16; i++)
        {
            var id = new LandblockId((uint)((0xA0 + i) << 24 | 0xB4 << 16 | 0xFFFF));
            ids[i] = id;
            arr[id.LandblockX, id.LandblockY] = (ACE.Server.Entity.Landblock)RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.Entity.Landblock));
        }
        var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        Func<int, long> real = t => { long n = 0; var id = ids[t % 16]; var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 400) { for (int i = 0; i < 256; i++) { if (LandblockManager.GetLandblock(id, false, false) == null) throw new Exception(); } n += 256; } return n; };
        Func<int, long> readLock = t => { long n = 0; var id = ids[t % 16]; var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 400) { for (int i = 0; i < 256; i++) { rw.EnterReadLock(); var lb = arr[id.LandblockX, id.LandblockY]; rw.ExitReadLock(); if (lb == null) throw new Exception(); } n += 256; } return n; };
        Func<int, long> lockFree = t => { long n = 0; var id = ids[t % 16]; var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 400) { for (int i = 0; i < 256; i++) { var lb = Volatile.Read(ref arr[id.LandblockX, id.LandblockY]); if (lb == null) throw new Exception(); } n += 256; } return n; };

        foreach (var (name, f) in new (string, Func<int, long>)[] { ("REAL GetLandblock (upgradeable read lock)", real), ("same lookup under EnterReadLock", readLock), ("lock-free fast path (Volatile.Read)", lockFree) })
        {
            for (int w = 0; w < 3; w++) { f(0); Thread.Sleep(300); }
            foreach (var threads in new[] { 1, 2, 4 })
            {
                double best = 0; double cpuAt = 0;
                for (int rep = 0; rep < 3; rep++)
                {
                    var counts = new long[threads];
                    var ts = new Thread[threads];
                    var cpu0 = Process.GetCurrentProcess().TotalProcessorTime; var sw = Stopwatch.StartNew();
                    for (int t = 0; t < threads; t++) { int tt = t; ts[t] = new Thread(() => counts[tt] = f(tt)); ts[t].Start(); }
                    foreach (var th in ts) th.Join();
                    sw.Stop(); var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
                    double total = counts.Sum(); double perSec = total / sw.Elapsed.TotalSeconds;
                    if (perSec > best) { best = perSec; cpuAt = cpu * 1e6 / total; }
                }
                Console.WriteLine($"{name,-44} threads={threads}: {best / 1e6,8:F2} M calls/s total   {1e9 / best * threads,8:F1} ns wall per call per thread   {cpuAt,7:F1} ns CPU per call");
            }
        }
    }
}
