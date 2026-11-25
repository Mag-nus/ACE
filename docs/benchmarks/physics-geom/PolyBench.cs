using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Entity;

// Prototype layouts for Polygon. Algorithms copied VERBATIM from ACE.Server/Physics/Polygon.cs
// (polygon_hits_sphere_precise, check_walkable), only the data access changed.
public sealed class FlatPoly
{
    public System.Numerics.Plane Plane;
    public Vector3[] V;          // vertex origins, inline (no Vertex objects, no List)
    public FlatPoly(Polygon p)
    {
        Plane = p.Plane; V = new Vector3[p.Vertices.Count];
        for (int i = 0; i < V.Length; i++) V[i] = p.Vertices[i].Origin;
    }

    public bool HitsSpherePrecise(Vector3 center, float radius, ref Vector3 contactPoint)
    {
        var v = V; int n = v.Length;
        if (n == 0) return true;
        var dpPos = Vector3.Dot(Plane.Normal, center) + Plane.D;
        var rad = radius - PhysicsGlobals.EPSILON;
        if (Math.Abs(dpPos) > rad) return false;
        var diff = rad * rad - dpPos * dpPos;
        contactPoint = center - Plane.Normal * dpPos;
        var prevIdx = n - 1;
        for (var i = 0; i < n; i++)
        {
            var edge = v[i] - v[prevIdx];
            var disp = contactPoint - v[prevIdx];
            prevIdx = i;
            var cross = Vector3.Cross(Plane.Normal, edge);
            if (Vector3.Dot(disp, cross) >= 0.0f) continue;
            prevIdx = n - 1;
            for (var j = 0; j < n; j++)
            {
                edge = v[j] - v[prevIdx];
                disp = contactPoint - v[prevIdx];
                prevIdx = j;
                cross = Vector3.Cross(Plane.Normal, edge);
                var dispDot = Vector3.Dot(disp, cross);
                if (dispDot < 0.0f)
                {
                    if (cross.LengthSquared() * diff < dispDot * dispDot) return false;
                    var dispEdge = Vector3.Dot(disp, edge);
                    if (dispEdge >= 0.0f && dispEdge <= edge.LengthSquared()) return true;
                }
                if (disp.LengthSquared() <= diff) return true;
            }
            return false;
        }
        return true;
    }
}

public struct EdgeData
{
    public Vector3 Start;      // lastVertex.Origin
    public Vector3 Edge;       // vertex - lastVertex
    public Vector3 Cross;      // Cross(Plane.Normal, edge)
    public float EdgeLenSq;
    public float CrossLenSq;
}

public sealed class PrePoly
{
    public System.Numerics.Plane Plane;
    public EdgeData[] E;
    public PrePoly(Polygon p)
    {
        Plane = p.Plane; int n = p.Vertices.Count; E = new EdgeData[n];
        var prev = n - 1;
        for (int i = 0; i < n; i++)
        {
            var edge = p.Vertices[i] - p.Vertices[prev];   // identical expression to Polygon.cs
            var cross = Vector3.Cross(p.Plane.Normal, edge);
            E[i] = new EdgeData { Start = p.Vertices[prev].Origin, Edge = edge, Cross = cross, EdgeLenSq = edge.LengthSquared(), CrossLenSq = cross.LengthSquared() };
            prev = i;
        }
    }

    // single pass (O.3) + precomputed edges (O.2). Proven equivalent: see report.
    public bool HitsSpherePrecise(Vector3 center, float radius, ref Vector3 contactPoint)
    {
        var e = E;
        if (e.Length == 0) return true;
        var dpPos = Vector3.Dot(Plane.Normal, center) + Plane.D;
        var rad = radius - PhysicsGlobals.EPSILON;
        if (Math.Abs(dpPos) > rad) return false;
        var diff = rad * rad - dpPos * dpPos;
        contactPoint = center - Plane.Normal * dpPos;
        bool anyNeg = false;
        for (var j = 0; j < e.Length; j++)
        {
            ref readonly var ed = ref e[j];
            var disp = contactPoint - ed.Start;
            var dispDot = Vector3.Dot(disp, ed.Cross);
            if (dispDot < 0.0f)
            {
                anyNeg = true;
                if (ed.CrossLenSq * diff < dispDot * dispDot) return false;
                var dispEdge = Vector3.Dot(disp, ed.Edge);
                if (dispEdge >= 0.0f && dispEdge <= ed.EdgeLenSq) return true;
            }
            if (disp.LengthSquared() <= diff) return true;
        }
        return !anyNeg;
    }
}

public static class PolyBench
{
    static Polygon MakePoly(Random r, int nPts)
    {
        var p = new Polygon(0, nPts, CullMode.Landblock);
        var n = Vector3.Normalize(new Vector3((float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f));
        var t = Vector3.Normalize(Vector3.Cross(n, Math.Abs(n.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX));
        var b = Vector3.Cross(n, t);
        var c = new Vector3(r.Next(100), r.Next(100), r.Next(20));
        for (int i = 0; i < nPts; i++)
        {
            double a = -2 * Math.PI * i / nPts;
            var o = c + t * (float)(Math.Cos(a) * 2) + b * (float)(Math.Sin(a) * 2);
            p.Vertices[i] = new Vertex(o);
        }
        p.make_plane();
        return p;
    }

    public static void Run()
    {
        Env.Init();
        foreach (var (label, count, scatter) in new[] { ("HOT  (1,024 polys, cache-resident)", 1024, false), ("COLD (262,144 polys, scattered heap, random order)", 262144, true) })
        {
            var r = new Random(7);
            var polys = new Polygon[count];
            var junk = new List<object>();
            for (int i = 0; i < count; i++)
            {
                polys[i] = MakePoly(r, 4);
                if (scatter) junk.Add(new byte[r.Next(16, 400)]);   // interleave other long-lived objects, as the dat/landblock load does
            }
            var flat = new FlatPoly[count]; var pre = new PrePoly[count];
            for (int i = 0; i < count; i++) { flat[i] = new FlatPoly(polys[i]); pre[i] = new PrePoly(polys[i]); if (scatter) junk.Add(new byte[r.Next(16, 400)]); }
            // queries: sphere near the polygon plane so the vertex loop actually runs
            const int Q = 4096;
            var qi = new int[Q]; var qc = new Vector3[Q]; var qr = new float[Q];
            for (int k = 0; k < Q; k++)
            {
                int i = r.Next(count); qi[k] = i;
                var p = polys[i];
                var centroid = Vector3.Zero; foreach (var v in p.Vertices) centroid += v.Origin; centroid /= p.Vertices.Count;
                qc[k] = centroid + p.Plane.Normal * ((float)r.NextDouble() * 0.4f - 0.2f) + new Vector3((float)r.NextDouble() * 6 - 3, (float)r.NextDouble() * 6 - 3, (float)r.NextDouble() * 6 - 3) * 0.6f;
                qr[k] = 0.5f;
            }
            // correctness
            int mism = 0, hits = 0;
            for (int k = 0; k < Q; k++)
            {
                var s = new Sphere(qc[k], qr[k]);
                Vector3 c1 = default, c2 = default, c3 = default;
                bool a = polys[qi[k]].polygon_hits_sphere_precise(s, ref c1);
                bool b = flat[qi[k]].HitsSpherePrecise(qc[k], qr[k], ref c2);
                bool c = pre[qi[k]].HitsSpherePrecise(qc[k], qr[k], ref c3);
                if (a != b || a != c || (a && (c1 != c2 || c1 != c3))) mism++;
                if (a) hits++;
            }
            Console.WriteLine($"== polygon_hits_sphere_precise  {label}: {hits}/{Q} hit, mismatches vs real = {mism}");
            var spheres = new Sphere[Q]; for (int k = 0; k < Q; k++) spheres[k] = new Sphere(qc[k], qr[k]);
            var a1 = Timer.Time("real ACE Polygon (List<Vertex> of class Vertex)", 8_000_000, n => { float s = 0; Vector3 cp = default; for (int i = 0; i < n; i++) { int k = i & (Q - 1); if (polys[qi[k]].polygon_hits_sphere_precise(spheres[k], ref cp)) s++; } return s; });
            var a2 = Timer.Time("FlatPoly (Vector3[] inline, same algorithm)", 8_000_000, n => { float s = 0; Vector3 cp = default; for (int i = 0; i < n; i++) { int k = i & (Q - 1); if (flat[qi[k]].HitsSpherePrecise(qc[k], qr[k], ref cp)) s++; } return s; });
            var a3 = Timer.Time("PrePoly (precomputed EdgeData[] + single pass)", 8_000_000, n => { float s = 0; Vector3 cp = default; for (int i = 0; i < n; i++) { int k = i & (Q - 1); if (pre[qi[k]].HitsSpherePrecise(qc[k], qr[k], ref cp)) s++; } return s; });
            Console.WriteLine($"  ratios: real/flat {a1.ns / a2.ns:F2}x   real/pre {a1.ns / a3.ns:F2}x");
            GC.KeepAlive(junk);
        }

        Console.WriteLine();
        Console.WriteLine("== retained bytes per 4-vertex polygon, by layout ==");
        var rr = new Random(3);
        var src = new Polygon[2000]; for (int i = 0; i < src.Length; i++) src[i] = MakePoly(rr, 4);
        int idx = 0;
        var real = Mem.AllocRetained(() => { var p = src[idx++ % src.Length]; var q = new Polygon(0, 4, CullMode.Landblock); for (int i = 0; i < 4; i++) q.Vertices[i] = new Vertex(p.Vertices[i].Origin); q.make_plane(); return q; }, 2000);
        Console.WriteLine($"  real ACE Polygon (terrain ctor + 4 Vertex): {real.retained} B   (dat ctor differs: VertexIDs list shared with dat, see mem mode)");
        idx = 0; var f = Mem.AllocRetained(() => new FlatPoly(src[idx++ % src.Length]), 2000);
        Console.WriteLine($"  FlatPoly (Plane + Vector3[4]):              {f.retained} B");
        idx = 0; var pp = Mem.AllocRetained(() => new PrePoly(src[idx++ % src.Length]), 2000);
        Console.WriteLine($"  PrePoly (Plane + EdgeData[4]):              {pp.retained} B");
    }
}
