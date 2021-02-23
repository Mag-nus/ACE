using System;
using System.IO;

namespace ACE.DatLoader
{
    public class DatDirectoryHeader : IUnpackable, IPackable
    {
        internal static readonly uint ObjectSize = ((sizeof(uint) * 0x3E) + sizeof(uint) + (DatFile.ObjectSize * 0x3D));

        public uint[] Branches { get; } = new uint[0x3E];
        public DatFile[] Entries { get; set; }

        public void Unpack(BinaryReader reader)
        {
            for (int i = 0; i < Branches.Length; i++)
                Branches[i] = reader.ReadUInt32();

            var entryCount = reader.ReadUInt32();

            Entries = new DatFile[entryCount];

            for (int i = 0; i < Entries.Length; i++)
            {
                Entries[i] = new DatFile();
                Entries[i].Unpack(reader);
            }
        }

        public void Pack(BinaryWriter writer)
        {
            if (Entries.Length > 0x3D)
                throw new ArgumentOutOfRangeException();

            for (int i = 0; i < Branches.Length; i++)
                writer.Write((UInt32)Branches[i]);

            writer.Write((UInt32)Entries.Length);

            foreach (var entry in Entries)
                entry.Pack(writer);
        }
    }
}
