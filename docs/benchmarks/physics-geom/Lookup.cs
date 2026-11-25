using System;
using System.Collections.Concurrent;
using ACE.Server.Physics.Common;

public static class Lookup
{
    public static void Run()
    {
        Env.Init();
        var lbs = new LandblockStruct(MakeClb());
        var dict = lbs.LandCells;                 // real ConcurrentDictionary<int, ObjCell>(1, 64) from InitPVArrays
        var arr = new ObjCell[64]; foreach (var kv in dict) arr[kv.Key] = kv.Value;
        var idx = new int[1024]; var r = new Random(1); for (int i = 0; i < idx.Length; i++) idx[i] = r.Next(64);
        var a = Timer.Time("LandCells.TryGetValue (ConcurrentDictionary<int,ObjCell>)", 20_000_000, n => { int s = 0; for (int i = 0; i < n; i++) { dict.TryGetValue(idx[i & 1023], out var c); s += c == null ? 0 : 1; } return s; });
        var b = Timer.Time("ObjCell[64] index", 20_000_000, n => { int s = 0; for (int i = 0; i < n; i++) { var c = arr[idx[i & 1023]]; s += c == null ? 0 : 1; } return s; });
        Console.WriteLine($"  ratio {a.ns / b.ns:F1}x");
    }
    public static ACE.DatLoader.FileTypes.CellLandblock MakeClb()
    {
        var clb = new ACE.DatLoader.FileTypes.CellLandblock();
        typeof(ACE.DatLoader.FileTypes.FileType).GetProperty("Id")!.SetValue(clb, 0xA9B4FFFFu);
        for (int i = 0; i < 81; i++) { clb.Height.Add((byte)(i % 50)); clb.Terrain.Add((ushort)(i % 7 << 2)); }
        return clb;
    }
}
