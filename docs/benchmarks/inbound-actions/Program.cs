using System;
using System.Diagnostics;
using System.Threading;

static partial class Program
{
    static void Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var mode = args.Length > 0 ? args[0] : "appraise";
        B.Bootstrap();
        switch (mode)
        {
            case "appraise": Appraise.Run(); break;
            case "find": Find.Run(); break;
            case "move": Move.Run(); break;
            case "proto": Proto.Run(); break;
            case "extra": Extra.Run(); break;
            case "count": Count.Run(); break;
            default: Console.WriteLine("modes: appraise | find | move | proto | extra | count"); break;
        }
    }

    /// Warm-up passes separated by sleeps so tiered JIT (and OSR/PGO re-jit) settles before measuring.
    public static (double bytes, double ns) Measure(Action a, int iters = 20000, int warm = 3000)
    {
        for (int r = 0; r < 3; r++)
        {
            for (int i = 0; i < warm; i++) a();
            Thread.Sleep(300);
        }
        for (int i = 0; i < warm; i++) a();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) a();
        sw.Stop();
        long after = GC.GetAllocatedBytesForCurrentThread();
        return ((after - before) / (double)iters, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
    }

    public static void P(string name, (double bytes, double ns) r, string extra = "")
        => Console.WriteLine($"{name,-74} {r.bytes,8:F0} B {r.ns,9:F0} ns {extra}");

    public static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            var inner = e; while ((inner is System.Reflection.TargetInvocationException || inner is TypeInitializationException) && inner.InnerException != null) inner = inner.InnerException;
            Console.WriteLine($"!! {what}: BLOCKED by {inner.GetType().Name}: {inner.Message}");
            var st = inner.StackTrace?.Split('\n');
            if (st != null) for (int i = 0; i < Math.Min(14, st.Length); i++) Console.WriteLine("     " + st[i].Trim());
        }
    }
}
