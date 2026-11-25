using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Server.Physics;
using ACE.Server.Physics.BSP;

// Flattened physics BSP: nodes in one struct array, sphere inline, polygons deduplicated per tree.
// Traversal logic copied from BSPNode.sphere_intersects_poly / BSPLeaf.sphere_intersects_poly / Polygon.pos_hits_sphere.
public sealed class FlatBsp
{
    public struct FNode
    {
        public System.Numerics.Plane Plane;
        public Vector3 C; public float R; public bool HasSphere;
        public int Pos, Neg;          // -1 = null
        public int PolyStart, PolyCount;
        public bool IsLeaf;
    }
    public FNode[] Nodes;
    public int[] PolyIdx;
    public Polygon[] Polys;      // shared Polygon objects (API still hands Polygon refs back to callers)
    public PrePoly[] Pre;        // optional precomputed geometry, same indices

    readonly List<FNode> _n = new(); readonly List<int> _pi = new(); readonly Dictionary<Polygon, int> _dedupe = new(ReferenceEqualityComparer.Instance);
    readonly List<Polygon> _polys = new();

    public FlatBsp(BSPNode root)
    {
        Build(root);
        Nodes = _n.ToArray(); PolyIdx = _pi.ToArray(); Polys = _polys.ToArray();
        Pre = new PrePoly[Polys.Length]; for (int i = 0; i < Polys.Length; i++) Pre[i] = new PrePoly(Polys[i]);
    }
    int Build(BSPNode node)
    {
        if (node == null) return -1;
        int me = _n.Count; _n.Add(default);
        var f = new FNode { Plane = node.SplittingPlane, IsLeaf = node is BSPLeaf, PolyStart = _pi.Count, PolyCount = 0 };
        if (node.Sphere != null) { f.C = node.Sphere.Center; f.R = node.Sphere.Radius; f.HasSphere = true; }
        if (node.Polygons != null)
            foreach (var p in node.Polygons)
            {
                if (!_dedupe.TryGetValue(p, out var idx)) { idx = _polys.Count; _polys.Add(p); _dedupe[p] = idx; }
                _pi.Add(idx); f.PolyCount++;
            }
        f.Pos = Build(node.PosNode); f.Neg = Build(node.NegNode);
        _n[me] = f;
        return me;
    }

    static bool SphereIntersects(in FNode n, Vector3 c, float r)
    {
        var delta = c - n.C; var radSum = n.R + r;
        return delta.LengthSquared() < radSum * radSum;
    }

    public bool SphereIntersectsPoly(int ni, Sphere sp, Vector3 movement, ref Polygon hit, ref Vector3 contact, bool usePre)
    {
        var c = sp.Center; var r = sp.Radius;
        ref readonly var n = ref Nodes[ni];
        if (n.IsLeaf)
        {
            if (n.PolyCount == 0 || !SphereIntersects(n, c, r)) return false;
            for (int k = 0; k < n.PolyCount; k++)
            {
                int pi = PolyIdx[n.PolyStart + k];
                bool h;
                System.Numerics.Plane plane;
                if (usePre) { var pp = Pre[pi]; h = pp.HitsSpherePrecise(c, r, ref contact); plane = pp.Plane; }
                else { var p = Polys[pi]; h = p.polygon_hits_sphere_precise(sp, ref contact); plane = p.Plane; }
                if (h) hit = Polys[pi];
                if (Vector3.Dot(movement, plane.Normal) >= 0.0f) continue;
                if (h) return true;
            }
            return false;
        }
        if (!SphereIntersects(n, c, r)) return false;
        var dist = Vector3.Dot(n.Plane.Normal, c) + n.Plane.D;
        var reach = r - PhysicsGlobals.EPSILON;
        if (dist >= reach) return SphereIntersectsPoly(n.Pos, sp, movement, ref hit, ref contact, usePre);
        if (dist <= -reach) return SphereIntersectsPoly(n.Neg, sp, movement, ref hit, ref contact, usePre);
        if (n.Pos >= 0 && SphereIntersectsPoly(n.Pos, sp, movement, ref hit, ref contact, usePre)) return true;
        if (n.Neg >= 0 && SphereIntersectsPoly(n.Neg, sp, movement, ref hit, ref contact, usePre)) return true;
        return false;
    }
}

public static class BspBench
{
    public static void Run()
    {
        Env.Init();
        foreach (var (label, trees) in new[] { ("HOT  (1 tree)", 1), ("COLD (3,000 trees = 3,000 EnvCells, random)", 3000) })
        {
            var spec = new SynthDat.Spec { Vertices = 60, RenderPolys = 60, PhysicsPolys = 30, DrawingInterior = 40, PhysicsLeaves = 14, Portals = 3 };
            var dat = SynthDat.MakeDatCellStruct(spec);
            var real = new BSPTree[trees]; var flat = new FlatBsp[trees];
            var junk = new List<object>();
            for (int t = 0; t < trees; t++) { real[t] = new BSPTree(dat.PhysicsBSP, dat.PhysicsPolygons, dat.VertexArray); junk.Add(new byte[2000]); }
            for (int t = 0; t < trees; t++) flat[t] = new FlatBsp(real[t].RootNode);
            var r = new Random(9);
            const int Q = 4096;
            var qc = new Vector3[Q]; var qm = new Vector3[Q]; var qt = new int[Q]; var qs = new Sphere[Q];
            for (int k = 0; k < Q; k++)
            {
                qc[k] = new Vector3((float)r.NextDouble() * 12 - 6, (float)r.NextDouble() * 12 - 6, (float)r.NextDouble() * 4);
                qm[k] = new Vector3((float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f);
                qt[k] = r.Next(trees); qs[k] = new Sphere(qc[k], 0.48f);
            }
            int mism = 0, hits = 0;
            for (int k = 0; k < Q; k++)
            {
                Polygon h1 = null, h2 = null, h3 = null; Vector3 c1 = default, c2 = default, c3 = default;
                bool a = real[qt[k]].RootNode.sphere_intersects_poly(qs[k], qm[k], ref h1, ref c1);
                bool b = flat[qt[k]].SphereIntersectsPoly(0, qs[k], qm[k], ref h2, ref c2, false);
                bool c = flat[qt[k]].SphereIntersectsPoly(0, qs[k], qm[k], ref h3, ref c3, true);
                if (a != b || a != c || !ReferenceEquals(h1, h2) || !ReferenceEquals(h1, h3)) mism++;
                if (a) hits++;
            }
            Console.WriteLine($"== BSP sphere_intersects_poly {label}: hits {hits}/{Q}, mismatches {mism}; nodes/tree {flat[0].Nodes.Length}, unique polys {flat[0].Polys.Length}, leaf poly refs {flat[0].PolyIdx.Length}");
            var t1 = Timer.Time("real BSPNode tree (virtual, Sphere objects, List<Polygon>)", 2_000_000, n => { float s = 0; for (int i = 0; i < n; i++) { int k = i & (Q - 1); Polygon h = null; Vector3 cp = default; if (real[qt[k]].RootNode.sphere_intersects_poly(qs[k], qm[k], ref h, ref cp)) s++; } return s; });
            var t2 = Timer.Time("flat nodes + same Polygon objects", 2_000_000, n => { float s = 0; for (int i = 0; i < n; i++) { int k = i & (Q - 1); Polygon h = null; Vector3 cp = default; if (flat[qt[k]].SphereIntersectsPoly(0, qs[k], qm[k], ref h, ref cp, false)) s++; } return s; });
            var t3 = Timer.Time("flat nodes + PrePoly (precomputed edges)", 2_000_000, n => { float s = 0; for (int i = 0; i < n; i++) { int k = i & (Q - 1); Polygon h = null; Vector3 cp = default; if (flat[qt[k]].SphereIntersectsPoly(0, qs[k], qm[k], ref h, ref cp, true)) s++; } return s; });
            Console.WriteLine($"  ratios: real/flat-nodes {t1.ns / t2.ns:F2}x   real/flat+pre {t1.ns / t3.ns:F2}x");
            GC.KeepAlive(junk);
        }
    }
}
