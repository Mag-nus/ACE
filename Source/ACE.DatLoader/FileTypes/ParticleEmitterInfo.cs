using System;
using System.IO;
using System.Numerics;
using System.Text;
using ACE.Entity.Enum;

namespace ACE.DatLoader.FileTypes
{
    /// <summary>
    /// These are client_portal.dat files starting with 0x32. 
    /// </summary>
    [DatFileType(DatFileType.ParticleEmitter)]
    public class ParticleEmitterInfo : FileType
    {
        public uint Unknown { get; private set; }
        public EmitterType EmitterType { get; private set; }
        public ParticleType ParticleType { get; private set; }
        public uint GfxObjId { get; private set; }
        public uint HwGfxObjId { get; private set; }
        public double Birthrate { get; private set; }
        public int MaxParticles { get; set; }
        public int InitialParticles { get; set; }
        public int TotalParticles { get; set; }
        public double TotalSeconds { get; set; }
        public double Lifespan { get; set; }
        public double LifespanRand { get; private set; }
        public Vector3 OffsetDir { get; private set; }
        public float MinOffset { get; private set; }
        public float MaxOffset { get; private set; }
        public Vector3 A { get; private set; }
        public float MinA { get; private set; }
        public float MaxA { get; private set; }
        public Vector3 B { get; private set; }
        public float MinB { get; private set; }
        public float MaxB { get; private set; }
        public Vector3 C { get; private set; }
        public float MinC { get; private set; }
        public float MaxC { get; private set; }
        public float StartScale { get; private set; }
        public float FinalScale { get; private set; }
        public float ScaleRand { get; private set; }
        public float StartTrans { get; private set; }
        public float FinalTrans { get; private set; }
        public float TransRand { get; private set; }
        public int IsParentLocal { get; private set; }

        public override void Unpack(BinaryReader reader)
        {
            Id = reader.ReadUInt32();

            Unknown = reader.ReadUInt32();

            EmitterType  =  (EmitterType)reader.ReadInt32();
            ParticleType = (ParticleType)reader.ReadInt32();

            GfxObjId   = reader.ReadUInt32();
            HwGfxObjId = reader.ReadUInt32();

            Birthrate = reader.ReadDouble();

            MaxParticles        = reader.ReadInt32();
            InitialParticles    = reader.ReadInt32();

            TotalParticles = reader.ReadInt32();

            TotalSeconds  = reader.ReadDouble();

            Lifespan     = reader.ReadDouble();
            LifespanRand = reader.ReadDouble();

            OffsetDir = reader.ReadVector3();      
            MinOffset = reader.ReadSingle();
            MaxOffset = reader.ReadSingle(); 

            A       = reader.ReadVector3();
            MinA    = reader.ReadSingle();
            MaxA    = reader.ReadSingle();

            B       = reader.ReadVector3();
            MinB    = reader.ReadSingle();
            MaxB    = reader.ReadSingle();

            C       = reader.ReadVector3();
            MinC    = reader.ReadSingle();
            MaxC    = reader.ReadSingle();

            StartScale = reader.ReadSingle();
            FinalScale = reader.ReadSingle();
            ScaleRand  = reader.ReadSingle();

            StartTrans = reader.ReadSingle();
            FinalTrans = reader.ReadSingle();
            TransRand  = reader.ReadSingle();

            IsParentLocal = reader.ReadInt32();
        }

        public override void Pack(BinaryWriter writer)
        {
            writer.Write((UInt32)Id);

            writer.Write((UInt32)Unknown);

            writer.Write((Int32)EmitterType);
            writer.Write((Int32)ParticleType);

            writer.Write((UInt32)GfxObjId);
            writer.Write((UInt32)HwGfxObjId);

            writer.Write((Double)Birthrate);

            writer.Write((Int32)MaxParticles);
            writer.Write((Int32)InitialParticles);

            writer.Write((Int32)TotalParticles);

            writer.Write((Double)TotalSeconds);

            writer.Write((Double)Lifespan);
            writer.Write((Double)LifespanRand);

            writer.Write(OffsetDir);
            writer.Write((Single)MinOffset);
            writer.Write((Single)MaxOffset);

            writer.Write(A);
            writer.Write((Single)MinA);
            writer.Write((Single)MaxA);

            writer.Write(B);
            writer.Write((Single)MinB);
            writer.Write((Single)MaxB);

            writer.Write(C);
            writer.Write((Single)MinC);
            writer.Write((Single)MaxC);

            writer.Write((Single)StartScale);
            writer.Write((Single)FinalScale);
            writer.Write((Single)ScaleRand);

            writer.Write((Single)StartTrans);
            writer.Write((Single)FinalTrans);
            writer.Write((Single)TransRand);

            writer.Write((Int32)IsParentLocal);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine("------------------");
            sb.AppendLine($"ID: {Id:X8}");
            sb.AppendLine("EmitterType: " + EmitterType);
            sb.AppendLine("ParticleType: " + ParticleType);
            sb.AppendLine($"GfxObjID: {GfxObjId:X8}");
            sb.AppendLine($"HWGfxObjID: {HwGfxObjId:X8}");
            sb.AppendLine("Birthrate: " + Birthrate);
            sb.AppendLine("MaxParticles: " + MaxParticles);
            sb.AppendLine("InitialParticles: " + InitialParticles);
            sb.AppendLine("TotalParticles: " + TotalParticles);
            sb.AppendLine("TotalSeconds: " + TotalSeconds);
            sb.AppendLine("Lifespan: " + Lifespan);
            sb.AppendLine("LifespanRand: " + LifespanRand);
            sb.AppendLine("OffsetDir: " + OffsetDir);
            sb.AppendLine("MinOffset: " + MinOffset);
            sb.AppendLine("MaxOffset: " + MaxOffset);
            sb.AppendLine("A: " + A);
            sb.AppendLine("MinA: " + MinA);
            sb.AppendLine("MaxA: " + MaxA);
            sb.AppendLine("B: " + B);
            sb.AppendLine("MinB: " + MinB);
            sb.AppendLine("MaxB: " + MaxB);
            sb.AppendLine("C: " + C);
            sb.AppendLine("MinC: " + MinC);
            sb.AppendLine("MaxC: " + MaxC);
            sb.AppendLine("StartScale: " + StartScale);
            sb.AppendLine("FinalScale: " + FinalScale);
            sb.AppendLine("ScaleRand: " + ScaleRand);
            sb.AppendLine("StartTrans: " + StartTrans);
            sb.AppendLine("FinalTrans: " + FinalTrans);
            sb.AppendLine("TransRand: " + TransRand);
            sb.AppendLine("IsParentLocal: " + IsParentLocal);

            return sb.ToString();
        }
    }
}
