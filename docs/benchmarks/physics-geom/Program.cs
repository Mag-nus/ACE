using System;

public static class Program
{
    public static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "mem";
        switch (mode)
        {
            case "mem": Mem.Run(); break;
            case "math": MathBench.Run(); break;
            case "poly": PolyBench.Run(); break;
            case "cache": CacheBench.Run(); break;
            case "box": BoxCheck.Run(); break;
            case "setup": SetupMem.Run(); break;
            case "bsp": BspBench.Run(); break;
            case "lookup": Lookup.Run(); break;
            case "celllist": CellListBench.Run(); break;
            default: Console.WriteLine("modes: mem | math | poly | cache | box | setup | bsp | lookup | celllist"); break;
        }
    }
}
