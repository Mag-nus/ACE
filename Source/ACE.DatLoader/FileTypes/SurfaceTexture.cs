using System;
using System.Collections.Generic;
using System.IO;

namespace ACE.DatLoader.FileTypes
{
    [DatFileType(DatFileType.SurfaceTexture)]
    public class SurfaceTexture : FileType
    {
        public int Unknown { get; private set; }
        public byte UnknownByte { get; private set; }
        public List<uint> Textures { get; private set; } = new List<uint>(); // These values correspond to a Surface (0x06) entry

        public override void Unpack(BinaryReader reader)
        {
            Id          = reader.ReadUInt32();

            Unknown     = reader.ReadInt32();
            UnknownByte = reader.ReadByte();

            Textures.Unpack(reader);
        }

        public override void Pack(BinaryWriter writer)
        {
            writer.Write((UInt32)Id);

            writer.Write((Int32) Unknown);
            writer.Write((Byte)UnknownByte);

            Textures.Pack(writer);
        }
    }
}
