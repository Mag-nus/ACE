using System;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.Sequence;
using ACE.Server.Network.Structure;

static class Parts
{
    static void P(string name, (double bytes, double ns) r) => Console.WriteLine($"{name,-60} alloc={r.bytes,7:F0} B  {r.ns,8:F1} ns");

    public static void Run()
    {
        var wo = Stubs.MakeWorldObject();
        Console.WriteLine("=== fixed per-message overhead ===");
        P("new MemoryStream(16)", Program.Measure(() => new MemoryStream(16)));
        P("new MemoryStream(16) + new BinaryWriter(ms)", Program.Measure(() => new BinaryWriter(new MemoryStream(16))));
        P("new MemoryStream() + BinaryWriter + Write(uint)  [default cap]", Program.Measure(() => { var w = new BinaryWriter(new MemoryStream()); w.Write(1u); }));
        P("byte[16]", Program.Measure(() => { var b = new byte[16]; GC.KeepAlive(b); }));

        Console.WriteLine("=== sequences ===");
        P("Sequences.GetCurrentSequence(ObjectInstance) (byte[2])", Program.Measure(() => wo.Sequences.GetCurrentSequence(SequenceType.ObjectInstance)));
        P("Sequences.GetNextSequence(UpdatePropertyInt, prop) (byte[1])", Program.Measure(() => wo.Sequences.GetNextSequence(SequenceType.UpdatePropertyInt, ACE.Entity.Enum.Properties.PropertyInt.CombatMode)));
        P("Sequences.GetSequence(ObjectInstance,0) (no bytes)", Program.Measure(() => wo.Sequences.GetSequence(SequenceType.ObjectInstance, 0)));

        Console.WriteLine("=== PositionPack / motion intermediates ===");
        P("new PositionPack(wo)", Program.Measure(() => new PositionPack(wo)));
        P("new Origin(cell, pos)", Program.Measure(() => new Origin(1, Vector3.One)));
        P("wo.Location (x1)", Program.Measure(() => { var l = wo.Location; }));
        P("new Motion(NonCombat, Ready)", Program.Measure(() => new Motion(MotionStance.NonCombat, MotionCommand.Ready)));
        var motion = new Motion(MotionStance.NonCombat, MotionCommand.Ready);
        P("new MovementData(wo, motion)", Program.Measure(() => new MovementData(wo, motion)));

        Console.WriteLine("=== strings (WriteString16L path) ===");
        var ms = new MemoryStream(4096); var bw = new BinaryWriter(ms);
        string s14 = "Drudge Skulker", s40 = "Anyone selling a Pyreal Mote? PST please";
        P("Encoding.GetEncoding(1252)", Program.Measure(() => Encoding.GetEncoding(1252)));
        var enc = Encoding.GetEncoding(1252);
        P("cp1252.GetBytes(string 40)", Program.Measure(() => enc.GetBytes(s40)));
        P("WriteString16L(14 ch) [current]", Program.Measure(() => { ms.Position = 0; bw.WriteString16L(s14); }));
        P("WriteString16L(40 ch) [current]", Program.Measure(() => { ms.Position = 0; bw.WriteString16L(s40); }));
        P("WriteString16L_Proto(40 ch)", Program.Measure(() => { ms.Position = 0; Proto.WriteString16L(bw, s40); }));
        P("WriteString16L_Proto(14 ch)", Program.Measure(() => { ms.Position = 0; Proto.WriteString16L(bw, s14); }));
        P("Pad(2) [current]", Program.Measure(() => { ms.Position = 0; bw.Pad(2); }));
        P("Pad(0) [current]", Program.Measure(() => { ms.Position = 0; bw.Pad(0); }));
        P("Align() [current, len%4=0 -> pad 0]", Program.Measure(() => { ms.SetLength(4); ms.Position = 4; bw.Align(); }));
        P("Align() [current, len%4=2]", Program.Measure(() => { ms.SetLength(2); ms.Position = 2; bw.Align(); }));
        P("WritePackedDword(100) [current]", Program.Measure(() => { ms.Position = 0; bw.WritePackedDword(100); }));
        P("WritePackedDword(100000) [current]", Program.Measure(() => { ms.Position = 0; bw.WritePackedDword(100000); }));
        P("WritePackedDword_Proto(100000)", Program.Measure(() => { ms.Position = 0; Proto.WritePackedDword(bw, 100000); }));

        Console.WriteLine("=== GetEncoding(1252) under 4-thread contention (ratio vs cached static, same run) ===");
        Contention();
    }

    static void Contention()
    {
        const int N = 2_000_000;
        Encoding cached = Encoding.GetEncoding(1252);
        double Run(Func<Encoding> f)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ => { Encoding e = null; for (int i = 0; i < N; i++) e = f(); GC.KeepAlive(e); });
            return sw.Elapsed.TotalMilliseconds;
        }
        Run(() => Encoding.GetEncoding(1252)); Run(() => cached);
        double a = Run(() => Encoding.GetEncoding(1252));
        double b = Run(() => cached);
        double single = 0;
        { var sw = System.Diagnostics.Stopwatch.StartNew(); Encoding e = null; for (int i = 0; i < 4 * N; i++) e = Encoding.GetEncoding(1252); single = sw.Elapsed.TotalMilliseconds; GC.KeepAlive(e); }
        Console.WriteLine($"4 threads x {N:N0} GetEncoding(1252): {a:F0} ms; same work single-threaded: {single:F0} ms; 4 threads cached static: {b:F0} ms");
    }
}
