using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

/// <summary>One scenario = one moving object + a steering delegate. Step() = steer, advance the clock, PhysicsObj.update_object().</summary>
public sealed class Scn
{
    public string Name;
    public string Note = "";
    public PhysicsObj Obj;
    public World.Region R;
    public double Dt = 0.2;
    public Action Steer;

    public void Step()
    {
        Steer?.Invoke();
        World.SetTime(World.Time + Dt);
        Obj.update_object();
    }

    public Vector2 Global()
    {
        var p = Obj.Position;
        return new Vector2(((int)(p.ObjCellID >> 24) - R.Bx0) * 192f + p.Frame.Origin.X, ((int)((p.ObjCellID >> 16) & 0xFF) - R.By0) * 192f + p.Frame.Origin.Y);
    }
}

public static class Scenarios
{
    public const uint SetupCreature = 0x02000001;      // 2 spheres + 1 cylsphere, 17 parts, no free heading
    public const uint SetupMissileSmall = 0x02000002;  // 1 sphere r=0.1, 1 part
    public const uint SetupMissileBig = 0x02000003;    // 1 sphere r=0.3, 1 part
    public const uint SetupCreatureFree = 0x02000004;  // as creature, AllowFreeHeading
    public const uint SetupSphereOnly = 0x02000005;    // 2 spheres, no cylsphere (sphere-shaped obstacle)
    public const uint SetupCreature1Part = 0x02000006;
    public const uint SetupCreature34Parts = 0x02000007;
    public const uint AnimRun = 0x03000001;

    public const PhysicsState CreatureState = PhysicsState.ReportCollisions | PhysicsState.Gravity;
    public const PhysicsState MissileState = PhysicsState.ReportCollisions | PhysicsState.Missile | PhysicsState.AlignPath | PhysicsState.PathClipped | PhysicsState.Gravity;

    public static World.Region Flat, Slope, Hills;
    static bool built;

    static readonly (Vector3, float)[] HumanSpheres = { (new Vector3(0, 0, 0.48f), 0.48f), (new Vector3(0, 0, 1.35f), 0.48f) };
    static readonly (Vector3, float, float)[] HumanCyl = { (new Vector3(0, 0, 0), 0.4f, 1.8f) };

    public static void BuildWorlds()
    {
        if (built) return;
        built = true;
        World.Init();
        World.SeedSetup(new World.SetupSpec { Id = SetupCreature, Parts = 17, Spheres = HumanSpheres, CylSpheres = HumanCyl });
        World.SeedSetup(new World.SetupSpec { Id = SetupCreatureFree, Parts = 17, Spheres = HumanSpheres, CylSpheres = HumanCyl, AllowFreeHeading = true });
        World.SeedSetup(new World.SetupSpec { Id = SetupCreature1Part, Parts = 1, Spheres = HumanSpheres, CylSpheres = HumanCyl });
        World.SeedSetup(new World.SetupSpec { Id = SetupCreature34Parts, Parts = 34, Spheres = HumanSpheres, CylSpheres = HumanCyl });
        World.SeedSetup(new World.SetupSpec { Id = SetupSphereOnly, Parts = 17, Spheres = HumanSpheres });
        World.SeedSetup(new World.SetupSpec { Id = SetupMissileSmall, Parts = 1, Spheres = new[] { (Vector3.Zero, 0.1f) }, AllowFreeHeading = true, Height = 0.2f, Radius = 0.1f, StepUp = 0.01f, StepDown = 0.01f });
        World.SeedSetup(new World.SetupSpec { Id = SetupMissileBig, Parts = 1, Spheres = new[] { (Vector3.Zero, 0.3f) }, AllowFreeHeading = true, Height = 0.6f, Radius = 0.3f, StepUp = 0.01f, StepDown = 0.01f });
        World.SeedAnimation(AnimRun, 17, 20, false);

        Flat = World.Build(0x20, 0x20, 4, 4, (x, y) => 20f);
        // tent along X: 9.46 degree slope (1 m per 6 m), walkable (FloorZ 0.664 = 48.4 degrees)
        Slope = World.Build(0x30, 0x20, 3, 3, (x, y) => 5f + (288f - MathF.Abs(x - 288f)) / 6f);
        // rolling hills, max slope ~10/60
        Hills = World.Build(0x40, 0x20, 8, 8, (x, y) => 30f + 10f * MathF.Sin(x / 60f) * MathF.Cos(y / 75f));
    }

    public static Quaternion Heading(float deg) { var f = new AFrame(); f.set_heading(deg); return f.Orientation; }
    public static Quaternion HeadingOf(Vector2 dir) => Heading(MathF.Atan2(dir.X, dir.Y) * 180f / MathF.PI);

    public static PhysicsObj Creature(World.Region r, float gx, float gy, float headingDeg = 0, uint setup = SetupCreature, bool monster = true, bool movementManager = true)
    {
        var o = Objects.Make(setup, CreatureState, creature: true, monster: monster, movementManager: movementManager);
        if (!Objects.Place(o, r, gx, gy, headingDeg)) throw new Exception($"placement failed at {gx},{gy}");
        return o;
    }

    /// <summary>Attaches a cyclic synthetic animation (20 frames, 30 fps, no pos frames) the way MotionTable would: real AnimSequenceNode(AnimData) -> DBObj.GetAnimation.</summary>
    public static void AttachRunAnimation(PhysicsObj o)
    {
        var ad = new AnimData { AnimID = AnimRun, LowFrame = 0, HighFrame = -1, Framerate = 30f };
        o.PartArray.Sequence.append_animation(ad);
    }

    /// <summary>Creature walking back and forth along dir around center (region metres); movement comes from Sequence.Velocity, as an animation's MotionData.Velocity does.</summary>
    public static Scn Walk(string name, World.Region r, Vector2 center, Vector2 dir, float half, float speed = 4f, double dt = 0.2, uint setup = SetupCreature, bool anim = false, bool monster = true)
    {
        dir = Vector2.Normalize(dir);
        var start = center - dir * half * 0.5f;
        var o = Creature(r, start.X, start.Y, 0, setup, monster);
        if (anim) AttachRunAnimation(o);
        o.PartArray.Sequence.SetVelocity(new Vector3(0, speed, 0));
        var s = new Scn { Name = name, Obj = o, R = r, Dt = dt };
        var qf = HeadingOf(dir); var qb = HeadingOf(-dir);
        bool fwd = true;
        s.Steer = () =>
        {
            float d = Vector2.Dot(s.Global() - center, dir);
            if (fwd) { if (d > half) fwd = false; }
            else if (d < -half) fwd = true;
            o.Position.Frame.Orientation = fwd ? qf : qb;
        };
        return s;
    }

    public static Scn Standing(string name, World.Region r, Vector2 at, bool movementManager, double dt = 0.2)
    {
        var o = Creature(r, at.X, at.Y, 0, SetupCreature, true, movementManager);
        return new Scn { Name = name, Obj = o, R = r, Dt = dt };
    }

    /// <summary>
    /// Missile with gravity: launched on a ballistic arc and re-launched (direct field writes, no allocation) before it lands, flying back and forth.
    /// </summary>
    public static Scn Missile(string name, World.Region r, Vector2 center, Vector2 dir, float speed, uint setup, double dt = 1.0 / 30 + 1e-4, int flightUpdates = 26)
    {
        dir = Vector2.Normalize(dir);
        var start = center - dir * (speed * (float)dt * flightUpdates * 0.5f);
        var o = Objects.Make(setup, MissileState, creature: false, monster: false);
        if (!Objects.Place(o, r, start.X, start.Y, 0, 1.5f)) throw new Exception("missile placement failed");
        float z0 = o.Position.Frame.Origin.Z;
        float vz = 9.8f * (float)dt * flightUpdates * 0.5f;       // returns to launch height after flightUpdates updates
        var s = new Scn { Name = name, Obj = o, R = r, Dt = dt };
        int k = 0; bool fwd = true;
        void Launch()
        {
            var d = fwd ? dir : -dir;
            o.Position.Frame.Origin.Z = z0;
            o.Velocity = new Vector3(d.X * speed, d.Y * speed, vz);
            o.State |= PhysicsState.Missile | PhysicsState.AlignPath | PhysicsState.PathClipped;
            o.TransientState |= TransientStateFlags.Active;
        }
        Launch();
        s.Steer = () =>
        {
            if (++k >= flightUpdates) { k = 0; fwd = !fwd; Launch(); }
        };
        return s;
    }

    /// <summary>Places n standing creature-shaped obstacles on a grid; returns how many were placed.</summary>
    public static List<PhysicsObj> Crowd(World.Region r, IEnumerable<Vector2> spots, uint setup = SetupCreature)
    {
        var list = new List<PhysicsObj>();
        foreach (var p in spots)
        {
            var o = Objects.Make(setup, CreatureState, creature: true, monster: true, movementManager: true);
            if (Objects.Place(o, r, p.X, p.Y, 0)) list.Add(o);
        }
        return list;
    }

    public static IEnumerable<Vector2> Grid(Vector2 origin, int n, int perRow, float dx, float dy)
    {
        for (int i = 0; i < n; i++)
            yield return origin + new Vector2((i % perRow) * dx, (i / perRow) * dy);
    }

    // ------------------------------------------------------------------ the scenario catalogue

    /// <summary>
    /// Each scenario gets its own landblock neighbourhood so they do not see each other's objects.
    /// Cell (lx 4, ly 5) of a block spans x 96..120, y 120..144; its centre is (108, 132).
    /// </summary>
    public static Scn Create(string id)
    {
        BuildWorlds();
        // block-local anchor inside Flat: block (1,1) -> offset 192
        Vector2 B(int bx, int by, float x, float y) => new Vector2(bx * 192f + x, by * 192f + y);
        switch (id)
        {
            case "a":       // inside one land cell, flat
                return Walk("a  creature, flat, inside one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f);
            case "a-free":
                return Walk("a  creature (AllowFreeHeading setup), flat, one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f, setup: SetupCreatureFree);
            case "a-anim":
                return Walk("a  creature + cyclic 30 fps animation, flat, one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f, anim: true);
            case "a-1part":
                return Walk("a  creature with 1 part, flat, one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f, setup: SetupCreature1Part);
            case "a-34parts":
                return Walk("a  creature with 34 parts, flat, one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f, setup: SetupCreature34Parts);
            case "a-moveto":   // as a, with the real MoveToManager.HandleMoveToPosition running every sub-step (state hand-seeded, see WalkMoveTo)
                return WalkMoveTo("a  creature with a MoveToPosition in progress (MoveToManager.UseTime live), flat, one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f);
            case "a-roamed":   // as a, after the object has walked through 256 distinct cells: PhysicsObj.ShadowObjects is never pruned
            {
                var s = Walk("a  creature, flat, one cell, after touring 256 cells", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f);
                var way = new List<Vector2>();
                for (int j = 0; j < 16; j++)
                {
                    float y = 192f + 12f + 24f * j;
                    way.Add(new Vector2(j % 2 == 0 ? 192f + 12f : 192f + 372f, y));
                    way.Add(new Vector2(j % 2 == 0 ? 192f + 372f : 192f + 12f, y));
                }
                way.Add(B(1, 1, 108, 130));
                foreach (var w in way)
                    for (int guard = 0; guard < 5000; guard++)
                    {
                        var d = w - s.Global();
                        if (d.Length() < 1.0f) break;
                        s.Obj.Position.Frame.Orientation = HeadingOf(d);
                        World.SetTime(World.Time + 0.2);
                        s.Obj.update_object();
                    }
                s.Note = $"ShadowObjects.Count={s.Obj.ShadowObjects.Count}";
                return s;
            }
            case "a-fast":
                return Walk("a  creature at 7 m/s (2 steps per transition), flat, one cell", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f, speed: 7f);
            case "a-edge":  // walking along a cell edge: 2 cells in every cell array
                return Walk("a2 creature, flat, along a cell edge (2 cells)", Flat, B(1, 1, 96.2f, 132), new Vector2(0, 1), 6f);
            case "a-30hz":  // same walk, driven at 30 Hz (1 UpdateObjectInternal per update, 0.13 m)
                return Walk("a  creature, flat, one cell, dt 1/30", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f, dt: 1.0 / 30 + 1e-4);
            case "b":       // crossing a cell boundary (y = 120 inside block 1,1)
                return Walk("b  creature, flat, crossing a cell boundary", Flat, B(1, 1, 108, 120), new Vector2(0, 1), 6f);
            case "b-diag":  // crossing through a cell corner region (4 cells)
                return Walk("b2 creature, flat, diagonal through a cell corner", Flat, B(1, 1, 96, 120), new Vector2(1, 1), 6f);
            case "c":       // crossing a landblock boundary (y = 192)
                return Walk("c  creature, flat, crossing a landblock boundary", Flat, B(1, 1, 108, 192), new Vector2(0, 1), 6f);
            case "c-diag":  // through a landblock corner (4 landblocks)
                return Walk("c2 creature, flat, diagonal through a landblock corner", Flat, B(2, 2, 0, 0), new Vector2(1, 1), 6f);
            case "d":       // up and down a 9.5 degree slope inside one cell
                return Walk("d  creature, 9.5 deg slope, inside one cell", Slope, B(0, 1, 108, 132), new Vector2(1, 0), 6f);
            case "d-cross": // slope, crossing a cell boundary
                return Walk("d2 creature, 9.5 deg slope, crossing a cell boundary", Slope, B(0, 1, 120, 132), new Vector2(1, 0), 6f);
            case "d-hills": // rolling terrain, long diagonal
                return Walk("d3 creature, rolling hills, 90 m diagonal", Hills, B(3, 3, 96, 96), new Vector2(1, 0.7f), 45f);
            case "e":       // standing still, creature with MovementManager (stays Active)
                return Standing("e  creature standing still (MovementManager => stays Active)", Flat, B(1, 1, 108, 132), true);
            case "e-inactive":
                return Standing("e2 object standing still, no MovementManager (goes inactive)", Flat, B(1, 1, 108, 132), false);
            case "m":       // missile, small sphere
                return Missile("B  missile r=0.1, 20 m/s, 30 Hz, flat", Flat, B(1, 1, 108, 96), new Vector2(0, 1), 20f, SetupMissileSmall);
            case "m-big":
                return Missile("B  missile r=0.3, 20 m/s, 30 Hz, flat", Flat, B(1, 1, 108, 96), new Vector2(0, 1), 20f, SetupMissileBig);
            case "m-big-slow":
                return Missile("B  missile r=0.3, 8 m/s (1 step per transition), 30 Hz, flat", Flat, B(1, 1, 108, 96), new Vector2(0, 1), 8f, SetupMissileBig);
        }
        if (id.StartsWith("f-near-"))       // passing a crowd without contact
        {
            int n = int.Parse(id.Substring(7));
            // obstacles east of the path, nearest row 2 m away (clearance 1.12 m), 1.2 m pitch, all inside the walker's cell
            var crowd = Crowd(Flat, Grid(B(1, 1, 110f, 126.5f), n, 10, 1.0f, 1.2f));
            var s = Walk($"f  creature passing {crowd.Count} cylsphere objects in its cell, no contact", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f);
            s.Note = $"crowd={crowd.Count}";
            return s;
        }
        if (id.StartsWith("f-slide-"))      // pressed against a wall of obstacles, sliding along it
        {
            int n = int.Parse(id.Substring(8));
            int wall = Math.Min(n, 11);
            var spots = new List<Vector2>();
            for (int i = 0; i < wall; i++) spots.Add(B(1, 1, 108f + (i - (wall - 1) * 0.5f) * 1.0f, 136f));     // wall along X at y = 136, 1.0 m pitch
            spots.AddRange(Grid(B(1, 1, 99f, 138f), n - wall, 10, 1.0f, 1.2f));                                    // the rest stands behind the wall
            var crowd = Crowd(Flat, spots);
            float half = Math.Max(0.4f, (wall - 1) * 0.5f - 0.7f);
            var s = WalkDiag($"f  creature sliding along a wall, {crowd.Count} cylsphere objects in its cell", Flat, B(1, 1, 108, 134.6f), half);
            s.Note = $"crowd={crowd.Count} wall={wall}";
            return s;
        }
        if (id.StartsWith("f-sph-near-"))   // sphere-shaped obstacles (no cylsphere)
        {
            int n = int.Parse(id.Substring(11));
            var crowd = Crowd(Flat, Grid(B(1, 1, 110.2f, 126.5f), n, 10, 1.0f, 1.2f), SetupSphereOnly);
            var s = Walk($"f  creature passing {crowd.Count} sphere objects in its cell, no contact", Flat, B(1, 1, 108, 132), new Vector2(0, 1), 6f);
            s.Note = $"crowd={crowd.Count}";
            return s;
        }
        if (id.StartsWith("b-pop-"))        // cell crossing with n other server objects in the landblock (visibility scan on cell change)
        {
            int n = int.Parse(id.Substring(6));
            var crowd = Crowd(Flat, Grid(B(1, 1, 20f, 20f), n, 20, 1.5f, 1.5f));    // far from the walker: different cells
            var s = Walk($"b  creature, flat, crossing a cell boundary, {crowd.Count} other objects in the landblock", Flat, B(1, 1, 108, 120), new Vector2(0, 1), 6f);
            s.Note = $"pop={crowd.Count}";
            return s;
        }
        throw new ArgumentException("unknown scenario " + id);
    }

    /// <summary>
    /// As Walk, plus a MoveToPosition "in progress" on the object's real MoveToManager, so that MovementManager.UseTime ->
    /// MoveToManager.UseTime -> HandleMoveToPosition runs for real in every UpdateObjectInternal, as it does for a chasing monster.
    /// MoveToManager.MoveToPosition itself cannot be used: BeginMoveForward -> _DoMotion needs a MotionTable (dat) and cancels the
    /// move without one. So the state it would leave behind is written by hand: one pending MoveToPosition node, CurrentCommand =
    /// RunForward, a far target straight ahead. Motion still comes from Sequence.Velocity.
    /// </summary>
    static Scn WalkMoveTo(string name, World.Region r, Vector2 center, Vector2 dir, float half, float speed = 4f)
    {
        var s = Walk(name, r, center, dir, half, speed);
        var o = s.Obj;
        var mtm = o.MovementManager.MoveToManager;
        dir = Vector2.Normalize(dir);
        var innerSteer = s.Steer;
        var qf = HeadingOf(dir);
        Position Target(bool fwd)
        {
            var g = center + (fwd ? dir : -dir) * (half + 40f);
            var (cell, origin) = r.At(g.X, g.Y, 0.005f);
            return new Position(cell, new AFrame(origin, Quaternion.Identity));
        }
        var tf = Target(true); var tb = Target(false);
        void Arm(bool fwd)
        {
            mtm.MovementType = ACE.Entity.Enum.MovementType.MoveToPosition;
            mtm.CurrentTargetPosition = fwd ? tf : tb;
            mtm.SoughtPosition = fwd ? tf : tb;
            mtm.StartingPosition = new Position(o.Position);
            mtm.MovementParams.FailDistance = float.MaxValue;
            mtm.MovementParams.Sticky = false;
            if (mtm.PendingActions.Count == 0) mtm.PendingActions.Add(new MovementNode(ACE.Entity.Enum.MovementType.MoveToPosition));
            mtm.CurrentCommand = (uint)ACE.Entity.Enum.MotionCommand.RunForward;
            mtm.AuxCommand = 0;
            mtm.MovingAway = false;
            mtm.PreviousDistance = mtm.OriginalDistance = mtm.GetCurrentDistance();
            mtm.PreviousDistanceTime = mtm.OriginalDistanceTime = World.Time;
        }
        bool lastFwd = true; Arm(true);
        s.Steer = () =>
        {
            innerSteer();
            bool fwd = o.Position.Frame.Orientation == qf;
            if (fwd != lastFwd || mtm.PendingActions.Count == 0) { lastFwd = fwd; Arm(fwd); }
        };
        return s;
    }

    /// <summary>Walker pushing north-east / north-west into a wall at y = const, sliding east / west along it.</summary>
    static Scn WalkDiag(string name, World.Region r, Vector2 center, float half)
    {
        var o = Creature(r, center.X, center.Y - 1.5f, 0);
        o.PartArray.Sequence.SetVelocity(new Vector3(0, 4f, 0));
        var s = new Scn { Name = name, Obj = o, R = r };
        var qe = Heading(50f); var qw = Heading(310f);
        bool east = true;
        s.Steer = () =>
        {
            float d = s.Global().X - center.X;
            if (east) { if (d > half) east = false; }
            else if (d < -half) east = true;
            o.Position.Frame.Orientation = east ? qe : qw;
        };
        return s;
    }

    public static readonly string[] All =
    {
        "a", "a-edge", "b", "b-diag", "c", "c-diag", "d", "d-cross", "d-hills", "e", "e-inactive",
        "f-near-0", "f-near-5", "f-near-20", "f-near-50", "f-slide-5", "f-slide-20", "f-slide-50",
        "m", "m-big", "m-big-slow",
        "a-free", "a-anim", "a-moveto", "a-roamed", "a-1part", "a-34parts", "a-fast", "a-30hz", "f-sph-near-20", "b-pop-50", "b-pop-200",
    };

    // ------------------------------------------------------------------ long random walk for the identical-results check

    public sealed class RandomWalk
    {
        public Scn S;
        public int Obstacles;
    }

    static List<PhysicsObj> rwObstacles;

    /// <summary>
    /// Deterministic random walk over the Hills region (3x3 inner landblocks = 24x24 cells) seeded with standing obstacles:
    /// random heading / speed changes, turns back at the border. Same seed => same inputs.
    /// </summary>
    public static RandomWalk MakeRandomWalk(int seed, int obstacles = 1500)
    {
        BuildWorlds();
        var r = Hills;
        float lo = 2 * 192f + 24f, hi = 5 * 192f - 24f;      // inner 3x3 landblocks, one cell of margin
        if (rwObstacles == null)
        {
            var rnd0 = new Random(12345);
            var spots = new List<Vector2>();
            for (int i = 0; i < obstacles; i++)
                spots.Add(new Vector2(lo + (float)rnd0.NextDouble() * (hi - lo), lo + (float)rnd0.NextDouble() * (hi - lo)));
            // keep the start area free
            spots.RemoveAll(p => Vector2.Distance(p, new Vector2(672f, 672f)) < 3f);
            rwObstacles = Crowd(r, spots);
        }
        var o = Creature(r, 672f, 672f, 0);
        var s = new Scn { Name = "random walk (hills, obstacles)", Obj = o, R = r };
        var rnd = new Random(seed);
        int left = 0;
        float[] speeds = { 0f, 2f, 4f, 4f, 4f, 6.5f };
        s.Steer = () =>
        {
            if (--left > 0) return;
            left = 3 + rnd.Next(20);
            var g = s.Global();
            float heading = (float)rnd.NextDouble() * 360f;
            float speed = speeds[rnd.Next(speeds.Length)];
            if (g.X < lo + 30 || g.X > hi - 30 || g.Y < lo + 30 || g.Y > hi - 30)
            {
                var toCentre = new Vector2(672f, 672f) - g;
                heading = MathF.Atan2(toCentre.X, toCentre.Y) * 180f / MathF.PI;
                if (heading < 0) heading += 360f;
                speed = 4f; left = 40;
            }
            o.Position.Frame.Orientation = Heading(heading);
            o.PartArray.Sequence.SetVelocity(new Vector3(0, speed, 0));
        };
        return new RandomWalk { S = s, Obstacles = rwObstacles.Count };
    }
}
