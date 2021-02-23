using System;
using System.Collections.Generic;
using System.IO;

namespace ACE.DatLoader
{
    public class DatDirectory
    {
        public uint RootSectorOffset { get; private set;  }

        private uint blockSize { get; }


        public DatDirectoryHeader DatDirectoryHeader { get; } = new DatDirectoryHeader();

        public List<DatDirectory> Directories { get; } = new List<DatDirectory>();


        public DatDirectory(uint rootSectorOffset, uint blockSize)
        {
            this.RootSectorOffset = rootSectorOffset;
            this.blockSize = blockSize;
        }

        public void Read(FileStream stream)
        {
            var headerReader = new DatReader(stream, RootSectorOffset, DatDirectoryHeader.ObjectSize, blockSize);

            using (var memoryStream = new MemoryStream(headerReader.Buffer))
            using (var reader = new BinaryReader(memoryStream))
                DatDirectoryHeader.Unpack(reader);

            // directory is allowed to have files + 1 subdirectories
            if (DatDirectoryHeader.Branches[0] != 0)
            {
                for (int i = 0; i < DatDirectoryHeader.Entries.Length + 1; i++)
                {
                    var directory = new DatDirectory(DatDirectoryHeader.Branches[i], blockSize);
                    directory.Read(stream);
                    Directories.Add(directory);
                }
            }
        }

        public void Write(FileStream stream)
        {
            RootSectorOffset = (uint)stream.Position;

            using (var memoryStream = new MemoryStream())
            using (var writer = new BinaryWriter(memoryStream))
            {
                DatDirectoryHeader.Pack(writer);

                var headerWriter = new DatWriter();

                headerWriter.Writer(stream, blockSize, memoryStream.ToArray());
            }

            for (int i = 0; i < Directories.Count; i++)
            {
                DatDirectoryHeader.Branches[i] = (uint)stream.Position;

                Directories[i].Write(stream);
            }
        }

        public void AddFilesToList(Dictionary<uint, DatFile> dicFiles)
        {
            Directories.ForEach(d => d.AddFilesToList(dicFiles));

            for (int i = 0; i < DatDirectoryHeader.Entries.Length; i++)
                dicFiles[DatDirectoryHeader.Entries[i].ObjectId] = DatDirectoryHeader.Entries[i];
        }

        private static void GetEntryRange(DatDirectory datDirectory, ref uint min, ref uint max)
        {
            foreach (var entry in datDirectory.DatDirectoryHeader.Entries)
            {
                if (entry.ObjectId == 0xFFFF0001)
                    ;

                if (entry.ObjectId < min)
                    min = entry.ObjectId;

                if (entry.ObjectId > max)
                    max = entry.ObjectId;
            }

            foreach (var directory in datDirectory.Directories)
                GetEntryRange(directory, ref min, ref max);
        }

        public void DumpToPath(string path)
        {
            for (int i = 0; i < DatDirectoryHeader.Entries.Length; i++)
                File.WriteAllText(Path.Combine(path, (i + 1).ToString().PadLeft(2, '0') + " " + DatDirectoryHeader.Entries[i].ObjectId.ToString("X8")), "");

            for (int i = 0; i < Directories.Count ; i++)
            {
                var min = uint.MaxValue;
                var max = uint.MinValue;
                GetEntryRange(Directories[i], ref min, ref max);

                var subPath = Path.Combine(path, "dir " + (i + 1).ToString().PadLeft(2, '0') + " " + min.ToString("X8") + " - " + max.ToString("X8"));

                Directory.CreateDirectory(subPath);

                Directories[i].DumpToPath(subPath);
            }
        }
    }
}
