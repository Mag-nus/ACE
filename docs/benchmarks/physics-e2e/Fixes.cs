using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

/// <summary>
/// The proposed fixes are measured as SOURCE-LEVEL variants of ACE.Server.dll (see make_variants.py): the harness is run from
/// out/&lt;variant&gt;/, whose ACE.Server.dll was compiled from a copy of the repo source with that fix applied. No Harmony is
/// involved in those runs, so the fixed code is inlined and optimised exactly like the original.
///
/// This class only keeps a Harmony TRANSPILER for PG.1, used by the in-process A/B (`pg1ab`) and by the `harmony` mode that
/// quantifies the bias of the earlier Harmony-instrumented micro-benchmark. A transpiler recompiles the method's own IL with
/// the one-line edit; add_all_outside_cells is far too large to be inlined, so no caller holds a stale copy.
/// </summary>
public static class Fixes
{
    static Harmony h;
    static readonly HashSet<string> active = new();

    public static readonly MethodInfo AddAllOutsideCellsSphere =
        AccessTools.Method(typeof(LandCell), "add_all_outside_cells", new[] { typeof(Position), typeof(int), typeof(List<Sphere>), typeof(CellArray) });

    /// <summary>Name of the ACE.Server.dll variant this process loaded (the directory the harness runs from).</summary>
    public static string Variant
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PHYS_VARIANT");
            if (!string.IsNullOrEmpty(env)) return env;
            return new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        }
    }

    public static string Label => active.Count == 0 ? Variant : Variant + "+harmony:" + string.Join("+", active.OrderBy(x => x));

    public static void Apply(IEnumerable<string> names)
    {
        foreach (var raw in names)
        {
            var n = raw.Trim().ToLowerInvariant();
            if (n.Length == 0 || n == "none") continue;
            switch (n)
            {
                case "pg1": Pg1(); break;
                case "pg1noop": Pg1Noop(); break;
                default: throw new ArgumentException("unknown harmony fix " + n + " (only pg1 / pg1noop; everything else is a source-built variant)");
            }
        }
    }

    public static void Pg1() { h ??= new Harmony("phys-e2e.fix"); h.Patch(AddAllOutsideCellsSphere, transpiler: new HarmonyMethod(typeof(Fixes), nameof(Pg1Transpiler))); active.Add("pg1"); }
    public static void Pg1Noop() { h ??= new Harmony("phys-e2e.fix"); h.Patch(AddAllOutsideCellsSphere, transpiler: new HarmonyMethod(typeof(Fixes), nameof(NoopTranspiler))); active.Add("pg1noop"); }
    public static void UnPg1() { h.Unpatch(AddAllOutsideCellsSphere, HarmonyPatchType.Transpiler, h.Id); active.Remove("pg1"); active.Remove("pg1noop"); }

    static IEnumerable<CodeInstruction> NoopTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;

    // LandCell.add_all_outside_cells(Position, int, List<Sphere>, CellArray): `cellArray.AddedOutside = true;` right after the early return.
    static IEnumerable<CodeInstruction> Pg1Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        var fAdded = AccessTools.Field(typeof(CellArray), nameof(CellArray.AddedOutside));
        int ret = list.FindIndex(ci => ci.opcode == OpCodes.Ret);
        // expected shape: ldarg.3; ldfld AddedOutside; brfalse L; ret; L: ...
        if (ret < 2 || ret > 4 || !list.Take(ret).Any(ci => ci.opcode == OpCodes.Ldfld && Equals(ci.operand, fAdded)))
            throw new Exception("PG.1 transpiler: unexpected IL at the start of add_all_outside_cells: " + string.Join(" | ", list.Take(6)));
        var target = list[ret + 1];
        var first = new CodeInstruction(OpCodes.Ldarg_3);
        first.labels.AddRange(target.labels);
        target.labels.Clear();
        list.InsertRange(ret + 1, new[] { first, new CodeInstruction(OpCodes.Ldc_I4_1), new CodeInstruction(OpCodes.Stfld, fAdded) });
        return list;
    }
}
