#!/usr/bin/env python3
"""
Builds source-level variants of ACE.Server.dll, one per proposed fix, from a COPY of the repo's Source tree.
The repo itself is only read. For every variant the touched files are re-copied pristine from the repo,
the edits below are applied (each must match exactly once), ACE.Server is rebuilt, and the resulting
ACE.Server.dll is stored in variants/<name>/.

usage: make_variants.py [variant ...]      (no args: all)
"""
import os, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_SRC = os.environ.get("ACE_SRC") or os.path.normpath(os.path.join(HERE, "..", "..", "..", "Source"))
COPY_SRC = os.path.join(HERE, "srcbuild", "Source")
PHYS = "ACE.Server/Physics"
BIN = os.path.join(COPY_SRC, "ACE.Server/bin/x64/Release/net10.0")
OUT = os.path.join(HERE, "variants")

FILES = [
    f"{PHYS}/Common/LandCell.cs", f"{PHYS}/Common/ObjCell.cs", f"{PHYS}/Common/CellArray.cs", f"{PHYS}/Common/Position.cs",
    f"{PHYS}/Common/LandblockStruct.cs",
    f"{PHYS}/PhysicsObj.cs", f"{PHYS}/Transition.cs", f"{PHYS}/SpherePath.cs", f"{PHYS}/CylSphere.cs", f"{PHYS}/Animation/AFrame.cs",
]

# ---------------------------------------------------------------- edits: (file, old, new)

PG1 = [(f"{PHYS}/Common/LandCell.cs",
"""        public static void add_all_outside_cells(Position position, int numSphere, List<Sphere> spheres, CellArray cellArray)
        {
            if (cellArray.AddedOutside) return;
""",
"""        public static void add_all_outside_cells(Position position, int numSphere, List<Sphere> spheres, CellArray cellArray)
        {
            if (cellArray.AddedOutside) return;

            cellArray.AddedOutside = true;
""")]

PC7 = [(f"{PHYS}/PhysicsObj.cs",
"""        public void report_collision_end(bool forceEnd)
        {
            if (CollisionTable == null) return;
""",
"""        public void report_collision_end(bool forceEnd)
        {
            if (CollisionTable == null || CollisionTable.Count == 0) return;
""")]

# PC.8: remove only the enumerator boxing. Same enumeration order, still re-walks from the start for every index,
# still sees cells appended while the caller's loop runs.
PC8 = [
(f"{PHYS}/Common/CellArray.cs",
"""        public void remove_cell(ObjCell cell)
""",
"""        public ObjCell GetCellAt(int index)
        {
            foreach (var cell in Cells.Values)
            {
                if (index == 0) return cell;
                index--;
            }
            throw new System.ArgumentOutOfRangeException(nameof(index));
        }

        public void remove_cell(ObjCell cell)
"""),
(f"{PHYS}/Common/ObjCell.cs", "var cell = cellArray.Cells.Values.ElementAt(i);", "var cell = cellArray.GetCellAt(i);"),
(f"{PHYS}/Transition.cs", "var cell = CellArray.Cells.Values.ElementAt(i);", "var cell = CellArray.GetCellAt(i);"),
(f"{PHYS}/PhysicsObj.cs", "var cell = cellArray.Cells.Values.ElementAt(i);", "var cell = cellArray.GetCellAt(i);"),
]

PC11 = [(f"{PHYS}/Animation/AFrame.cs",
"""            Orientation *= Quaternion.CreateFromYawPitchRoll(rotation.X, rotation.Y, rotation.Z);
            Orientation  = Quaternion.Normalize(Orientation);
""",
"""            if (rotation != Vector3.Zero)
                Orientation *= Quaternion.CreateFromYawPitchRoll(rotation.X, rotation.Y, rotation.Z);
            Orientation  = Quaternion.Normalize(Orientation);
""")]

PG2 = [(f"{PHYS}/CylSphere.cs",
"""            path.CacheLocalSpaceSphere(pos, 1.0f);
            var global_cylsphere = new CylSphere(LowPoint, Height, Radius, scale);
""",
"""            path.LocalSpacePos.ObjCellID = pos.ObjCellID;
            path.LocalSpacePos.Frame.Origin = pos.Frame.Origin;
            path.LocalSpacePos.Frame.Orientation = pos.Frame.Orientation;
            var global_cylsphere = new CylSphere(LowPoint, Height, Radius, scale);
""")]

COPYFROM = (f"{PHYS}/Common/Position.cs",
"""        public void Init()
        {
            Frame = new AFrame();
        }
""",
"""        public void Init()
        {
            Frame = new AFrame();
        }

        public void CopyFrom(Position p)
        {
            ObjCellID = p.ObjCellID;
            Frame.Origin = p.Frame.Origin;
            Frame.Orientation = p.Frame.Orientation;
        }
""")

# PC.2: alias-guarded in-place SetCheckPos / RestoreCheckPos / SetCurrentCheckPos; BackupCheckPos always copied in place.
PC2 = [
COPYFROM,
(f"{PHYS}/SpherePath.cs",
"""        public void RestoreCheckPos()
        {
            CheckPos = new Position(BackupCheckPos);
""",
"""        public void RestoreCheckPos()
        {
            if (ReferenceEquals(CheckPos, CurPos) || ReferenceEquals(CheckPos, BackupCheckPos))
                CheckPos = new Position(BackupCheckPos);
            else
                CheckPos.CopyFrom(BackupCheckPos);
"""),
(f"{PHYS}/SpherePath.cs",
"""        public void SaveCheckPos()
        {
            BackupCell = CheckCell;
            BackupCheckPos = new Position(CheckPos);
        }
""",
"""        public void SaveCheckPos()
        {
            BackupCell = CheckCell;
            BackupCheckPos.CopyFrom(CheckPos);
        }
"""),
(f"{PHYS}/SpherePath.cs",
"""        public void SetCheckPos(Position position, ObjCell cell)
        {
            CheckPos = new Position(position);
""",
"""        public void SetCheckPos(Position position, ObjCell cell)
        {
            if (ReferenceEquals(CheckPos, CurPos) || ReferenceEquals(CheckPos, position))
                CheckPos = new Position(position);
            else
                CheckPos.CopyFrom(position);
"""),
(f"{PHYS}/SpherePath.cs",
"""            Collide = true;
            BackupCell = CheckCell;
            BackupCheckPos = new Position(CheckPos);
""",
"""            Collide = true;
            BackupCell = CheckCell;
            BackupCheckPos.CopyFrom(CheckPos);
"""),
(f"{PHYS}/Transition.cs",
"""            SpherePath.BackupCheckPos = new Position(SpherePath.CheckPos);
            SpherePath.BackupCell = SpherePath.CheckCell;

            var stepHeight = ObjectInfo.StepDownHeight;
""",
"""            SpherePath.BackupCheckPos.CopyFrom(SpherePath.CheckPos);
            SpherePath.BackupCell = SpherePath.CheckCell;

            var stepHeight = ObjectInfo.StepDownHeight;
"""),
(f"{PHYS}/Transition.cs",
"""            SpherePath.BackupCell = SpherePath.CheckCell;
            SpherePath.BackupCheckPos = new Position(SpherePath.CheckPos);

            var stepDown = StepDown(stepDownHeight, zLandingValue);
""",
"""            SpherePath.BackupCell = SpherePath.CheckCell;
            SpherePath.BackupCheckPos.CopyFrom(SpherePath.CheckPos);

            var stepDown = StepDown(stepDownHeight, zLandingValue);
"""),
(f"{PHYS}/Transition.cs",
"""        public void SetCurrentCheckPos()
        {
            SpherePath.CurPos = new Position(SpherePath.CheckPos);
""",
"""        public void SetCurrentCheckPos()
        {
            if (ReferenceEquals(SpherePath.CurPos, SpherePath.CheckPos))
                SpherePath.CurPos = new Position(SpherePath.CheckPos);
            else
                SpherePath.CurPos.CopyFrom(SpherePath.CheckPos);
"""),
]

# NEGATIVE CONTROL: the unguarded in-place copy (P.3 as originally written), which Section Q PC.2 says is unsafe because
# ValidatePlacement / ValidatePlacementTransition alias CurPos = CheckPos. Used to show the identical-results checks can fail.
PC2NAIVE = [
COPYFROM,
(f"{PHYS}/SpherePath.cs",
"""        public void RestoreCheckPos()
        {
            CheckPos = new Position(BackupCheckPos);
""",
"""        public void RestoreCheckPos()
        {
            CheckPos.CopyFrom(BackupCheckPos);
"""),
(f"{PHYS}/SpherePath.cs",
"""            BackupCell = CheckCell;
            BackupCheckPos = new Position(CheckPos);
        }
""",
"""            BackupCell = CheckCell;
            BackupCheckPos.CopyFrom(CheckPos);
        }
"""),
(f"{PHYS}/SpherePath.cs",
"""        public void SetCheckPos(Position position, ObjCell cell)
        {
            CheckPos = new Position(position);
""",
"""        public void SetCheckPos(Position position, ObjCell cell)
        {
            CheckPos.CopyFrom(position);
"""),
(f"{PHYS}/Transition.cs",
"""        public void SetCurrentCheckPos()
        {
            SpherePath.CurPos = new Position(SpherePath.CheckPos);
""",
"""        public void SetCurrentCheckPos()
        {
            SpherePath.CurPos.CopyFrom(SpherePath.CheckPos);
"""),
]

# PC.3 Stage 0: in-place set_frame / set_current_pos, per-object scratch newPos + offset frame, in-place combine,
# copies at the two cold store sites (set_initial_frame, add_obj_to_cell) and at the failed-transition store.
PC3 = [
(f"{PHYS}/PhysicsObj.cs",
"""        public int InitialUpdates;

        public void UpdateObjectInternal(double quantum)
""",
"""        public int InitialUpdates;

        private Position scratchNewPos;
        private AFrame scratchOffsetFrame;

        public void UpdateObjectInternal(double quantum)
"""),
(f"{PHYS}/PhysicsObj.cs",
"""            var newPos = new Position(Position.ObjCellID);
            UpdatePositionInternal(quantum, ref newPos.Frame);
""",
"""            var newPos = scratchNewPos ??= new Position();
            newPos.ObjCellID = Position.ObjCellID;
            UpdatePositionInternal(quantum, ref newPos.Frame);
"""),
(f"{PHYS}/PhysicsObj.cs",
"""                        newPos.Frame.Origin = Position.Frame.Origin;
                        set_initial_frame(newPos.Frame);
                        CachedVelocity = Vector3.Zero;
""",
"""                        newPos.Frame.Origin = Position.Frame.Origin;
                        set_initial_frame(newPos.Frame);    // set_initial_frame copies (below)
                        CachedVelocity = Vector3.Zero;
"""),
(f"{PHYS}/PhysicsObj.cs",
"""        public void UpdatePositionInternal(double quantum, ref AFrame newFrame)
        {
            var offsetFrame = new AFrame();
""",
"""        public void UpdatePositionInternal(double quantum, ref AFrame newFrame)
        {
            var offsetFrame = scratchOffsetFrame ??= new AFrame();
            offsetFrame.Origin = Vector3.Zero;
            offsetFrame.Orientation = Quaternion.Identity;
"""),
(f"{PHYS}/PhysicsObj.cs",
"""            newFrame = AFrame.Combine(Position.Frame, offsetFrame);

            if (!State.HasFlag(PhysicsState.Hidden))
                UpdatePhysicsInternal((float)quantum, ref newFrame);
""",
"""            {
                var combinedOrigin = Position.Frame.Origin + Vector3.Transform(offsetFrame.Origin, Position.Frame.Orientation);
                var combinedOrientation = Quaternion.Multiply(Position.Frame.Orientation, offsetFrame.Orientation);
                newFrame.Origin = combinedOrigin;
                newFrame.Orientation = combinedOrientation;
            }

            if (!State.HasFlag(PhysicsState.Hidden))
                UpdatePhysicsInternal((float)quantum, ref newFrame);
"""),
(f"{PHYS}/PhysicsObj.cs",
"""            Position.Frame = new AFrame(frame);
            //Position.Frame.Origin = frame.Origin;
            //Position.Frame.Orientation = frame.Orientation;
""",
"""            Position.Frame.Origin = frame.Origin;
            Position.Frame.Orientation = frame.Orientation;
"""),
(f"{PHYS}/PhysicsObj.cs",
"""        public void set_initial_frame(AFrame frame)
        {
            Position.Frame = frame;
""",
"""        public void set_initial_frame(AFrame frame)
        {
            Position.Frame = new AFrame(frame);
"""),
(f"{PHYS}/PhysicsObj.cs",
"""            enter_cell(newCell);

            Position.Frame = newFrame;
""",
"""            enter_cell(newCell);

            Position.Frame = new AFrame(newFrame);
"""),
(f"{PHYS}/PhysicsObj.cs",
"""            Position.ObjCellID = newPos.ObjCellID;
            Position.Frame = new AFrame(newPos.Frame);
""",
"""            Position.ObjCellID = newPos.ObjCellID;
            Position.Frame.Origin = newPos.Frame.Origin;
            Position.Frame.Orientation = newPos.Frame.Orientation;
"""),
]

# PC.1: pooled Transition with the documented Reset. EMULATION of the allocation effect only: one instance per thread,
# reused by the next MakeTransition. (The real proposal uses a per-thread stack released at owner scopes.)
POOL = [(f"{PHYS}/Transition.cs",
"""        public static Transition MakeTransition()
        {
            return new Transition();
        }
""",
"""        [System.ThreadStatic] private static Transition pooledTransition;

        public static Transition MakeTransition()
        {
            var t = pooledTransition;
            if (t == null)
                return pooledTransition = new Transition();
            t.Reset();
            return t;
        }

        private static void ResetPos(Position p) { p.ObjCellID = 0; p.Frame.Origin = Vector3.Zero; p.Frame.Orientation = Quaternion.Identity; }

        // verbatim from docs/benchmarks/physics-core/Bench/TransitionPoolProto.cs
        public void Reset()
        {
            var t = this;
            var oi = t.ObjectInfo;
            oi.Object = null; oi.State = 0; oi.Scale = 0; oi.StepUpHeight = 0; oi.StepDownHeight = 0;
            oi.Ethereal = false; oi.StepDown = false; oi.TargetID = 0;

            var ci = t.CollisionInfo;
            ci.LastKnownContactPlaneValid = false; ci.LastKnownContactPlane = default; ci.LastKnownContactPlaneIsWater = false;
            ci.ContactPlaneValid = false; ci.ContactPlane = default; ci.ContactPlaneCellID = 0; ci.LastKnownContactPlaneCellID = 0;
            ci.ContactPlaneIsWater = false; ci.SlidingNormalValid = false; ci.SlidingNormal = default; ci.CollisionNormalValid = false;
            ci.CollisionNormal = default; ci.AdjustOffset = default; ci.NumCollideObject = 0; ci.CollideObject.Clear();
            ci.LastCollidedObject = null; ci.CollidedWithEnvironment = false; ci.FramesStationaryFall = 0; ci.VerifiedRestrictions = false;

            var ca = t.CellArray;
            ca.AddedOutside = false; ca.LoadCells = false; ca.Cells.Clear(); ca.NumCells = 0;

            var sp = t.SpherePath;
            sp.NumSphere = 0; sp.LocalSphere.Clear(); sp.LocalLowPoint = default; sp.GlobalSphere.Clear(); sp.GlobalLowPoint = default;
            sp.LocalSpaceSphere.Clear(); sp.LocalSpaceLowPoint = default; sp.LocalSpaceCurrCenter.Clear(); sp.GlobalCurrCenter.Clear();
            ResetPos(sp.LocalSpacePos); sp.LocalSpaceZ = default; sp.BeginCell = null; sp.BeginPos = null; sp.EndPos = null; sp.CurCell = null;
            sp.CurPos = new Position(); sp.CheckPos = new Position(); sp.BackupCheckPos = new Position();
            sp.GlobalOffset = default; sp.StepUp = false; sp.StepUpNormal = default; sp.Collide = false; sp.CheckCell = null;
            sp.InsertType = 0; sp.StepDown = false; sp.Backup = 0; sp.BackupCell = null; sp.ObstructionEthereal = false;
            sp.HitsInteriorCell = false; sp.BuildingCheck = false; sp.WalkableAllowance = 0; sp.WalkInterp = 0; sp.StepDownAmt = 0;
            sp.WalkableCheckPos = new Sphere(); sp.Walkable = null; sp.CheckWalkable = false; sp.WalkableUp = default; sp.WalkablePos = null;
            sp.WalkableScale = 0; sp.CellArrayValid = false; sp.NegStepUp = false; sp.NegCollisionNormal = default; sp.NegPolyHit = false;
            sp.PlacementAllowsSliding = true;
        }
""")]

# PG.7: the CellLandblock constructor no longer builds the 64 LandCells + dictionary that Generate() -> InitPVArrays() replaces.
PG7 = [(f"{PHYS}/Common/LandblockStruct.cs",
"""        public LandblockStruct(CellLandblock landblock)
        {
            Init();
            Height = landblock.Height;
""",
"""        public LandblockStruct(CellLandblock landblock)
        {
            TransDir = LandDefs.Direction.Unknown;
            WaterType = LandDefs.WaterType.NotWater;
            Height = landblock.Height;
""")]

VARIANTS = {
    "base": [],
    "pg1": PG1,
    "pc7": PC7,
    "pc8": PC8,
    "pc11": PC11,
    "q2": PC7 + PC8 + PC11,                              # Q.E item 2
    "pg2": PG2,
    "pc3": PC3,
    "pc2": PC2,
    "items1to5": PG1 + PC7 + PC8 + PC11 + PG2 + PC3 + PC2,  # Q.E items 1-5
    "pg7": PG7,
    "pc2naive": PC2NAIVE,
    "pool": POOL,
    "all": PG1 + PC7 + PC8 + PC11 + PG2 + PC3 + PC2 + POOL,
}


def run(cmd, **kw):
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, **kw)
    return r.returncode, r.stdout


def restore():
    for f in FILES:
        shutil.copyfile(os.path.join(REPO_SRC, f), os.path.join(COPY_SRC, f))


def build(name, edits):
    restore()
    cache = {}
    for f, old, new in edits:
        p = os.path.join(COPY_SRC, f)
        s = cache.get(p)
        if s is None:
            s = open(p, encoding="utf-8-sig", newline="").read()
        s = s.replace("\r\n", "\n")
        n = s.count(old)
        if n != 1:
            raise SystemExit(f"[{name}] edit in {f} matched {n} times (expected 1):\n{old}")
        cache[p] = s.replace(old, new)
    for p, s in cache.items():
        open(p, "w", encoding="utf-8", newline="").write(s.replace("\n", "\r\n"))    # the repo files are CRLF
    rc, out = run(["dotnet", "build", "ACE.Server/ACE.Server.csproj", "-c", "Release", "-p:Platform=x64", "-v", "q", "-nologo"], cwd=COPY_SRC)
    if rc != 0:
        print(out[-4000:])
        raise SystemExit(f"[{name}] build failed")
    d = os.path.join(OUT, name)
    os.makedirs(d, exist_ok=True)
    for ext in ("dll", "pdb"):
        shutil.copyfile(os.path.join(BIN, f"ACE.Server.{ext}"), os.path.join(d, f"ACE.Server.{ext}"))
    # keep the edited sources next to the binary for reference
    rc, out = run(["diff", "-ru", "--strip-trailing-cr", os.path.join(REPO_SRC, PHYS), os.path.join(COPY_SRC, PHYS)])
    # repo-relative paths, so each diff applies with `git apply -p1` from the repo root
    out = out.replace(REPO_SRC + "/", "a/Source/").replace(COPY_SRC + "/", "b/Source/").replace("diff -ru --strip-trailing-cr ", "diff -ru ")
    open(os.path.join(d, "source.diff"), "w").write(out)
    if name == "base":
        # a complete bin directory of the unmodified current source, used as AceBin for the harness build
        dst = os.path.join(HERE, "acebin-head")
        shutil.rmtree(dst, ignore_errors=True)
        shutil.copytree(BIN, dst)
    print(f"[{name}] built, {len(edits)} edits, diff {len(out.splitlines())} lines")


if __name__ == "__main__":
    names = sys.argv[1:] or list(VARIANTS)
    for n in names:
        build(n, VARIANTS[n])
    restore()
