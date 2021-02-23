using System;
using System.IO;

namespace ACE.DatLoader
{
    /// <summary>
    /// DiskFileInfo_t in the client
    /// </summary>
    public class DatDatabaseHeader : IUnpackable, IPackable
    {
        public uint FileType { get; private set; }
        public uint BlockSize { get; private set; }
        public uint FileSize { get; set; }
        public DatDatabaseType DataSet { get; private set; }
        public uint DataSubset { get; private set; }

        public uint FreeHead { get; set; }
        public uint FreeTail { get; set; }
        public uint FreeCount { get; set; }
        public uint BTree { get; set; }

        public uint NewLRU { get; private set; }
        public uint OldLRU { get; private set; }
        public bool UseLRU { get; private set; }

        public uint MasterMapID { get; private set; }

        public uint EnginePackVersion { get; private set; }
        public uint GamePackVersion { get; private set; }
        public byte[] VersionMajor { get; private set; } = new byte[16];
        public uint VersionMinor { get; private set; }

        public void Unpack(BinaryReader reader)
        {
            FileType    = reader.ReadUInt32();
            BlockSize   = reader.ReadUInt32();
            FileSize    = reader.ReadUInt32();
            DataSet     = (DatDatabaseType)reader.ReadUInt32();
            DataSubset  = reader.ReadUInt32();

            FreeHead    = reader.ReadUInt32();
            FreeTail    = reader.ReadUInt32();
            FreeCount   = reader.ReadUInt32();
            BTree       = reader.ReadUInt32();

            NewLRU      = reader.ReadUInt32();
            OldLRU      = reader.ReadUInt32();
            UseLRU      = (reader.ReadUInt32() == 1);

            MasterMapID = reader.ReadUInt32();

            EnginePackVersion   = reader.ReadUInt32();
            GamePackVersion     = reader.ReadUInt32();
            VersionMajor        = reader.ReadBytes(16);
            VersionMinor        = reader.ReadUInt32();
        }

        public void Pack(BinaryWriter writer)
        {
            writer.Write((UInt32)FileType);
            writer.Write((UInt32)BlockSize);
            writer.Write((UInt32)FileSize);
            writer.Write((UInt32)DataSet);
            writer.Write((UInt32)DataSubset);

            writer.Write((UInt32)FreeHead);
            writer.Write((UInt32)FreeTail);
            writer.Write((UInt32)FreeCount);
            writer.Write((UInt32)BTree);

            writer.Write((UInt32)NewLRU);
            writer.Write((UInt32)OldLRU);
            writer.Write((UInt32)(UseLRU ? 1 : 0));

            writer.Write((UInt32)MasterMapID);

            writer.Write((UInt32)EnginePackVersion);
            writer.Write((UInt32)GamePackVersion);
            writer.Write(VersionMajor);
            writer.Write((UInt32)VersionMinor);
        }
    }
}
