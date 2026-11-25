using System;
using System.Numerics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;

public static class BoxCheck
{
    public static void Run()
    {
        Env.Init();
        var r = new Random(11);
        foreach (var (label, maxTilt, fullRandom) in new[] { ("yaw only", 0.0, false), ("yaw + tilt <=15deg (terrain-aligned scenery)", 15.0, false), ("yaw + tilt <=45deg", 45.0, false), ("uniform random rotation", 0.0, true) })
        {
            int bad = 0; double worst = 0; const int N = 20000;
            for (int i = 0; i < N; i++)
            {
                Quaternion q;
                if (fullRandom) q = Quaternion.Normalize(new Quaternion((float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f));
                else
                {
                    var yaw = (float)(r.NextDouble() * Math.PI * 2);
                    var t = (float)(maxTilt * Math.PI / 180);
                    q = Quaternion.CreateFromYawPitchRoll((float)(r.NextDouble() * 2 - 1) * t, (float)(r.NextDouble() * 2 - 1) * t, 0) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw);
                    q = Quaternion.Normalize(q);
                }
                var from = new Position(0xA9B40001, new AFrame(new Vector3(50, 50, 10), q));
                var to = new Position(0xA9B40001, new AFrame(new Vector3(40, 60, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(r.NextDouble() * 6.28))));
                var src = new BBox { Min = new Vector3(-1, -2, 0), Max = new Vector3(1.5f, 2, 3) };
                var box = new BBox(); box.LocalToLocal(src, from, to);   // REAL ACE code
                double miss = 0;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 4) != 0 ? src.Max.X : src.Min.X, (c & 2) != 0 ? src.Max.Y : src.Min.Y, (c & 1) != 0 ? src.Max.Z : src.Min.Z);
                    var p = to.LocalToLocal(from, corner);
                    var below = Vector3.Max(box.Min - p, Vector3.Zero); var above = Vector3.Max(p - box.Max, Vector3.Zero);
                    miss = Math.Max(miss, Math.Max(below.Length(), above.Length()));
                }
                if (miss > 1e-3) bad++;
                worst = Math.Max(worst, miss);
            }
            Console.WriteLine($"  BBox.LocalToLocal result fails to contain a transformed corner: {bad,6}/{N} ({100.0 * bad / N:F1}%)  worst miss {worst:F3} m   [{label}]");
        }
    }
}
