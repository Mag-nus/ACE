using System;
using ACE.Server.Physics;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Entity;

public static class SetupMem
{
    public static void Run()
    {
        Env.Init();
        foreach (var nParts in new[] { 1, 3, 17 })
        {
            var model = ACE.DatLoader.FileTypes.SetupModel.CreateSimpleSetup();
            for (uint i = 0; i < nParts; i++)
            {
                uint id = 0x01000100u + i;
                GfxObjCache.GfxObjs.TryAdd(id, new GfxObj { ID = id });   // pre-seed cache: no dat access
                model.Parts.Add(id);
            }
            model.Spheres.Add(new ACE.DatLoader.Entity.Sphere());
            model.Spheres.Add(new ACE.DatLoader.Entity.Sphere());
            var s = Mem.AllocRetained(() => new Setup(model), 2000);
            Console.WriteLine($"  parts={nParts,2}: new Setup(model)                 alloc {s.alloc,6} B  retained {s.retained,6} B");
            var full = Mem.AllocRetained(() => { var pa = new PartArray(); pa.Setup = new Setup(model); pa.InitParts(); return pa; }, 2000);
            Console.WriteLine($"  parts={nParts,2}: PartArray + Setup + InitParts    alloc {full.alloc,6} B  retained {full.retained,6} B  (garbage {full.alloc - full.retained} B)");
            var pa1 = new PartArray(); pa1.Setup = new Setup(model); pa1.InitParts();
            bool aliased = ReferenceEquals(pa1.Parts[0].Pos, pa1.Setup.Parts[0].Pos);
            Console.WriteLine($"             PartArray.Parts[0].Pos is Setup.Parts[0].Pos (aliased): {aliased}");
        }
    }
}
