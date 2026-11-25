using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

public static class CacheBench
{
    static Quaternion RandQ(Random r) => Quaternion.Normalize(new Quaternion((float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f));

    public static void Run()
    {
        Env.Init();
        var r = new Random(5);
        // a mover: 2 spheres (player-like), CurPos/CheckPos in one landblock
        var path = new SpherePath();
        path.InitSphere(2, new List<Sphere> { new Sphere(new Vector3(0, 0, 0.48f), 0.48f), new Sphere(new Vector3(0, 0, 1.35f), 0.48f) }, 1.0f);
        path.CurPos = new Position(0xA9B40021, new AFrame(new Vector3(100, 100, 10), RandQ(r)));
        path.CheckPos = new Position(0xA9B40021, new AFrame(new Vector3(100.3f, 100.1f, 10), RandQ(r)));
        path.CacheGlobalSphere(null);

        const int N = 1024;
        var parts = new Position[N];
        for (int i = 0; i < N; i++)
            parts[i] = new Position(0xA9B40021, new AFrame(new Vector3(r.Next(80, 120), r.Next(80, 120), 10), RandQ(r)));

        Console.WriteLine("== SpherePath.CacheLocalSpaceSphere (REAL, NumSphere=2) ==");
        var real = Timer.Time("real path.CacheLocalSpaceSphere(pos, 1f)", 4_000_000, n => { for (int i = 0; i < n; i++) path.CacheLocalSpaceSphere(parts[i & (N - 1)], 1.0f); return path.LocalSpaceSphere[0].Center.X; });

        // (a) what CylSphere.StepSphereUp actually consumes: LocalSpacePos's orientation. Copy fields into a reused Position.
        var reused = new Position(0, new AFrame());
        var copy = Timer.Time("proto: copy pos into reused LocalSpacePos (no transforms)", 4_000_000, n => { for (int i = 0; i < n; i++) { var p = parts[i & (N - 1)]; reused.ObjCellID = p.ObjCellID; reused.Frame.Origin = p.Frame.Origin; reused.Frame.Orientation = p.Frame.Orientation; } return reused.Frame.Origin.X; });
        Console.WriteLine($"  ratio real/copy = {real.ns / copy.ns:F1}x");

        // (b) global-space pre-reject for a BSP part: part's PhysicsSphere (local) -> CheckPos space, vs GlobalSphere[0..1]
        var partSphere = new Sphere(new Vector3(0, 0, 1.0f), 1.5f);
        int rejects = 0;
        var rej = Timer.Time("proto: global-space bounding-sphere reject (1 transform)", 4_000_000, n =>
        {
            int rj = 0;
            for (int i = 0; i < n; i++)
            {
                var p = parts[i & (N - 1)];
                var c = path.CheckPos.LocalToGlobal(p, partSphere.Center * 1.0f);
                bool any = false;
                for (int s = 0; s < path.NumSphere; s++)
                {
                    var g = path.GlobalSphere[s];
                    var rs = partSphere.Radius + g.Radius + 0.01f;  // slack: exact test still runs when not rejected
                    if ((c - g.Center).LengthSquared() < rs * rs) { any = true; break; }
                }
                if (!any) rj++;
            }
            rejects = rj; return rj;
        });
        Console.WriteLine($"  ratio real/reject = {real.ns / rej.ns:F1}x   (rejected {rejects} of last run; parts placed within +-20 m)");

        // exactness of the reject vs the existing local-space test it guards (GfxObj.FindObjCollisions)
        int falseReject = 0, agree = 0;
        for (int i = 0; i < N; i++)
        {
            path.CacheLocalSpaceSphere(parts[i], 1.0f);
            bool localHit = false;
            foreach (var ls in path.LocalSpaceSphere)
            {
                var offset = partSphere.Center - ls.Center; var radsum = partSphere.Radius + ls.Radius;
                if (offset.LengthSquared() - radsum * radsum < PhysicsGlobals.EPSILON) { localHit = true; break; }
            }
            var c = path.CheckPos.LocalToGlobal(parts[i], partSphere.Center);
            bool globalPass = false;
            for (int s = 0; s < path.NumSphere; s++) { var g = path.GlobalSphere[s]; var rs = partSphere.Radius + g.Radius + 0.01f; if ((c - g.Center).LengthSquared() < rs * rs) globalPass = true; }
            if (localHit && !globalPass) falseReject++; else agree++;
        }
        Console.WriteLine($"  reject safety: false rejects {falseReject} / {N} (must be 0)");
    }
}
