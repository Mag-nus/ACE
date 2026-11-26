using System;
using System.Diagnostics;

namespace Bench
{
    public static class H
    {
        // Runs body(n) once for warmup and then `runs` times; reports the best ns/op and the bytes/op of the best run.
        // body must perform n operations. Allocation measured on the current thread only, unless allThreads.
        public static void Measure(string name, int n, Action<int> body, int runs = 5, bool allThreads = false)
        {
            body(Math.Max(1, n / 10)); // warmup / tier-up
            body(Math.Max(1, n / 10));
            double best = double.MaxValue; double bestAlloc = 0; double sumNs = 0;
            for (int r = 0; r < runs; r++)
            {
                long a0 = allThreads ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                body(n);
                sw.Stop();
                long a1 = allThreads ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
                double ns = sw.Elapsed.TotalMilliseconds * 1e6 / n;
                sumNs += ns;
                if (ns < best) { best = ns; bestAlloc = (double)(a1 - a0) / n; }
            }
            Console.WriteLine($"{name,-70} {best,10:F1} ns/op (mean {sumNs / runs,8:F1})  {bestAlloc,8:F1} B/op");
        }

        public static void Header(string s)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + s + " ===");
        }
    }
}
