using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

// Builds byte streams in the exact dat layout read by ACE.DatLoader.Entity.CellStruct.Unpack,
// so the REAL ACE.Server.Physics.Common.CellStruct constructor can be exercised without dat files.
// Geometry is synthetic (convex-ish ring of quads); counts are parameters.
public static class SynthDat
{
    public sealed class Spec
    {
        public int Vertices = 40;
        public int RenderPolys = 40;      // CellStruct.Polygons (drawing)
        public int PhysicsPolys = 20;     // CellStruct.PhysicsPolygons
        public int Portals = 2;
        public int DrawingInterior = 26;  // drawing BSP interior nodes (each holds InPolys)
        public int PhysicsLeaves = 10;    // physics BSP leaves (each holds InPolys)
        public double PhysLeafDup = 1.5;  // avg number of leaves each physics poly appears in
        public int CellPlanes = 8;        // cell BSP: convex chain of planes
        public bool HasDrawingBSP = true;
        public int UVsPerVertex = 1;
    }

    static void Tag(BinaryWriter w, string t)
    {
        var chars = t.ToCharArray(); Array.Reverse(chars);
        w.Write(Encoding.ASCII.GetBytes(chars));
    }
    static void Vec(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static void Plane(BinaryWriter w, Vector3 n, float d) { Vec(w, Vector3.Normalize(n)); w.Write(d); }
    static void Sphere(BinaryWriter w, Vector3 c, float r) { Vec(w, c); w.Write(r); }

    static void Align(BinaryWriter w) { while (w.BaseStream.Position % 4 != 0) w.Write((byte)0); }

    static void Polys(BinaryWriter w, int count, int numVerts, int keyBase)
    {
        for (int i = 0; i < count; i++)
        {
            w.Write((ushort)(keyBase + i));
            w.Write((byte)4);                 // NumPts (quad)
            w.Write((byte)0x4);               // Stippling NoPos -> no UV bytes
            w.Write((int)2);                  // CullMode.Clockwise
            w.Write((short)0); w.Write((short)0);
            int a = (i * 2) % numVerts;
            // a quad on the side wall: a, a+1, a+1+half, a+half (ring of vertices top/bottom)
            int half = numVerts / 2;
            int b = (a + 1) % half;
            int a0 = a % half;
            w.Write((short)a0); w.Write((short)b); w.Write((short)(b + half)); w.Write((short)(a0 + half));
            // NegUV: Clockwise and !NoNeg -> NumPts bytes
            for (int k = 0; k < 4; k++) w.Write((byte)0);
        }
    }

    // balanced tree writer; leafWriter writes a leaf; interiorExtra writes the per-type trailing data
    static int _leafIdx;
    static void Tree(BinaryWriter w, int interiorCount, Action<BinaryWriter> leaf, Action<BinaryWriter, int> interiorTail, ref int interiorUsed)
    {
        if (interiorCount <= 0) { leaf(w); return; }
        int myIndex = interiorUsed++;
        Tag(w, "BPIN");
        Plane(w, new Vector3(1, (myIndex % 3) - 1, 0.1f), myIndex * 0.5f);
        int rest = interiorCount - 1;
        int left = rest / 2, right = rest - left;
        Tree(w, left, leaf, interiorTail, ref interiorUsed);
        Tree(w, right, leaf, interiorTail, ref interiorUsed);
        interiorTail(w, myIndex);
    }

    public static byte[] CellStructBytes(Spec s)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)s.RenderPolys); w.Write((uint)s.PhysicsPolys); w.Write((uint)s.Portals);
        // CVertexArray
        w.Write((int)1); w.Write((uint)s.Vertices);
        int half = s.Vertices / 2;
        for (int i = 0; i < s.Vertices; i++)
        {
            w.Write((ushort)i);
            w.Write((ushort)s.UVsPerVertex);
            double ang = 2 * Math.PI * (i % half) / half;
            var o = new Vector3((float)(Math.Cos(ang) * 5), (float)(Math.Sin(ang) * 5), i < half ? 0f : 4f);
            Vec(w, o); Vec(w, Vector3.UnitZ);
            for (int k = 0; k < s.UVsPerVertex; k++) { w.Write(0f); w.Write(0f); }
        }
        Polys(w, s.RenderPolys, s.Vertices, 0);
        for (int i = 0; i < s.Portals; i++) w.Write((ushort)i);
        Align(w);
        // Cell BSP: convex chain, PosNode-only nodes ("BPnn" -> PosNode only)
        for (int i = 0; i < s.CellPlanes; i++)
        {
            Tag(w, i == s.CellPlanes - 1 ? "BPnn" : "BPnn");
            Plane(w, new Vector3((float)Math.Cos(i), (float)Math.Sin(i), 0), 6f);
        }
        Tag(w, "LEAF"); w.Write(0);
        // Physics polys
        Polys(w, s.PhysicsPolys, s.Vertices, 0);
        // Physics BSP
        int physInterior = s.PhysicsLeaves - 1;
        int polyCursor = 0;
        int perLeaf = (int)Math.Ceiling(s.PhysicsPolys * s.PhysLeafDup / s.PhysicsLeaves);
        int used = 0; _leafIdx = 0;
        Tree(w, physInterior,
            leaf =>
            {
                Tag(leaf, "LEAF"); leaf.Write(_leafIdx++); leaf.Write(1);
                Sphere(leaf, Vector3.Zero, 6f);
                leaf.Write((uint)perLeaf);
                for (int k = 0; k < perLeaf; k++) leaf.Write((ushort)((polyCursor++) % s.PhysicsPolys));
            },
            (tw, idx) => Sphere(tw, Vector3.Zero, 8f), ref used);
        // Drawing BSP
        w.Write(s.HasDrawingBSP ? 1u : 0u);
        if (s.HasDrawingBSP)
        {
            int perNode = Math.Max(1, (int)Math.Round((double)s.RenderPolys / s.DrawingInterior));
            int rc = 0; used = 0;
            Tree(w, s.DrawingInterior,
                leaf => { Tag(leaf, "LEAF"); leaf.Write(_leafIdx++); },
                (tw, idx) =>
                {
                    Sphere(tw, Vector3.Zero, 8f);
                    tw.Write((uint)perNode);
                    for (int k = 0; k < perNode; k++) tw.Write((ushort)((rc++) % s.RenderPolys));
                }, ref used);
        }
        Align(w);
        return ms.ToArray();
    }

    public static ACE.DatLoader.Entity.CellStruct MakeDatCellStruct(Spec s)
    {
        var bytes = CellStructBytes(s);
        var cs = new ACE.DatLoader.Entity.CellStruct();
        using var r = new BinaryReader(new MemoryStream(bytes));
        cs.Unpack(r);
        if (r.BaseStream.Position != bytes.Length) throw new Exception($"unpack consumed {r.BaseStream.Position} of {bytes.Length}");
        return cs;
    }
}
