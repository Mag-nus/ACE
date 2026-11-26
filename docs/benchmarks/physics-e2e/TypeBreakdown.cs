using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection;
using System.Threading;

/// <summary>
/// Allocation breakdown by type, in process:
///  (1) GCAllocationTick (GC keyword 0x1, Verbose): one event per ~100 KB allocated, carrying the type of the object that crossed
///      the threshold => a byte-weighted sample; share of ticks ~ share of bytes.
///  (2) GCSampledObjectAllocationHigh (keyword 0x200000): if the runtime honours it for an in-process listener it reports
///      object counts and bytes per type; printed when any arrive.
/// Only events raised on the measuring thread, between Start and Stop, are counted.
/// </summary>
public sealed class GcTypeListener : EventListener
{
    public readonly ConcurrentDictionary<string, long> TickCount = new();
    public readonly ConcurrentDictionary<string, long> TickBytes = new();
    public readonly ConcurrentDictionary<string, (long objs, long bytes)> Sampled = new();
    public long Ticks, SampledEvents, OtherThreadTicks;
    public volatile bool Recording;
    public long ThreadId = -1;
    readonly bool wantSampled;
    public readonly ConcurrentDictionary<string, int> EventNames = new();

    public GcTypeListener(bool sampled) { wantSampled = sampled; }

    EventSource runtime;
    protected override void OnEventSourceCreated(EventSource es)
    {
        if (es.Name == "Microsoft-Windows-DotNETRuntime") runtime = es;
    }

    public void Enable(bool sampled)
    {
        // base ctor may call OnEventSourceCreated before our fields are set; enable explicitly afterwards
        foreach (var es in EventSource.GetSources())
            if (es.Name == "Microsoft-Windows-DotNETRuntime") runtime = es;
        long kw = 0x1;                       // GC
        if (sampled) kw |= 0x200000 | 0x80000;   // GCSampledObjectAllocationHigh | Type
        EnableEvents(runtime, EventLevel.Verbose, (EventKeywords)kw);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!Recording || e.EventName == null) return;
        if (e.EventName.StartsWith("GCAllocationTick"))
        {
            if (ThreadId >= 0 && e.OSThreadId != ThreadId) { Interlocked.Increment(ref OtherThreadTicks); return; }
            string type = null; long amount = 0;
            for (int i = 0; i < e.PayloadNames.Count; i++)
            {
                var n = e.PayloadNames[i];
                if (n == "TypeName") type = e.Payload[i] as string;
                else if (n == "AllocationAmount64") amount = Convert.ToInt64(e.Payload[i]);
            }
            type ??= "?";
            Interlocked.Increment(ref Ticks);
            TickCount.AddOrUpdate(type, 1, (_, v) => v + 1);
            TickBytes.AddOrUpdate(type, amount, (_, v) => v + amount);
        }
        else if (e.EventName.StartsWith("GCSampledObjectAllocation"))
        {
            if (ThreadId >= 0 && e.OSThreadId != ThreadId) return;
            long typeId = 0, objs = 0, bytes = 0;
            for (int i = 0; i < e.PayloadNames.Count; i++)
            {
                var n = e.PayloadNames[i];
                if (n == "TypeID") typeId = unchecked((long)Convert.ToUInt64(e.Payload[i]));
                else if (n == "ObjectCountForTypeSample") objs = Convert.ToInt64(e.Payload[i]);
                else if (n == "TotalSizeForTypeSample") bytes = Convert.ToInt64(e.Payload[i]);
            }
            string name;
            try { name = Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr((IntPtr)typeId))?.ToString() ?? typeId.ToString("X"); }
            catch { name = typeId.ToString("X"); }
            Interlocked.Increment(ref SampledEvents);
            Sampled.AddOrUpdate(name, (objs, bytes), (_, v) => (v.objs + objs, v.bytes + bytes));
        }
    }
}

public static class TypeBreakdown
{
    static long CurrentOSThreadId()
    {
        try
        {
            var p = typeof(Thread).GetProperty("CurrentOSThreadId", BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null) return Convert.ToInt64(p.GetValue(null));
        }
        catch { }
        return -1;
    }

    public static void Run(string id, int megabytes)
    {
        var s = Scenarios.Create(id);
        Modes.Header();
        Bench.Warm(s);
        bool sampled = Environment.GetEnvironmentVariable("PHYS_SAMPLED") == "1";
        using var l = new GcTypeListener(sampled);
        l.Enable(sampled);
        l.ThreadId = CurrentOSThreadId();
        for (int i = 0; i < 2000; i++) s.Step();
        Thread.Sleep(200);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        long target = (long)megabytes << 20;
        long updates = 0;
        l.Recording = true;
        while (GC.GetAllocatedBytesForCurrentThread() - b0 < target)
        {
            for (int i = 0; i < 500; i++) s.Step();
            updates += 500;
        }
        long total = GC.GetAllocatedBytesForCurrentThread() - b0;
        Thread.Sleep(700);          // let the dispatcher drain
        l.Recording = false;

        Console.WriteLine($"{id}: {s.Name}");
        Console.WriteLine($"   {updates} updates, {total} B allocated on the measuring thread = {(double)total / updates:F1} B/update; {l.Ticks} GCAllocationTick samples on this thread ({l.OtherThreadTicks} on other threads ignored); thread filter {(l.ThreadId >= 0 ? "on" : "OFF")}");
        Console.WriteLine("   by type (GCAllocationTick, byte-weighted sample): share of samples  ~bytes/update");
        double perUpdate = (double)total / updates;
        foreach (var kv in l.TickCount.OrderByDescending(kv => kv.Value).Take(14))
        {
            double share = (double)kv.Value / l.Ticks;
            double se = Math.Sqrt(share * (1 - share) / l.Ticks);
            Console.WriteLine($"      {share * 100,5:F1}% +-{se * 100:F1}  {share * perUpdate,8:F0} B  {kv.Key}");
        }
        if (l.SampledEvents > 0)
        {
            long sb = l.Sampled.Values.Sum(v => v.bytes), so = l.Sampled.Values.Sum(v => v.objs);
            Console.WriteLine($"   GCSampledObjectAllocation: {l.SampledEvents} events, {so} objects, {sb} B ({(double)sb / total * 100:F1}% of thread bytes) => {(double)so / updates:F1} objects/update");
            foreach (var kv in l.Sampled.OrderByDescending(kv => kv.Value.bytes).Take(16))
                Console.WriteLine($"      {(double)kv.Value.bytes / sb * 100,5:F1}%  {(double)kv.Value.objs / updates,7:F2} objs/upd  {(double)kv.Value.bytes / updates,8:F1} B/upd  ({(double)kv.Value.bytes / kv.Value.objs:F0} B each)  {kv.Key}");
        }
        else if (sampled)
            Console.WriteLine("   GCSampledObjectAllocation: no events received");
    }
}
