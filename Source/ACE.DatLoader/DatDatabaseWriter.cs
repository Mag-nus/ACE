using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ACE.DatLoader
{
    public static class DatDatabaseWriter
    {
        public static void Save(DatDatabase datDatabase, string filePath)
        {
            var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);


            // First, we write all the DatFiles
            fileStream.Seek(datDatabase.Header.BlockSize, SeekOrigin.Begin);

            var datWriter = new DatWriter();

            foreach (var kvp in datDatabase.AllFiles)
            {
                byte[] buffer;

                // See if we can Pack this file first
                if (datDatabase.FileCache.TryGetValue(kvp.Key, out var fileType))
                {
                    try
                    {
                        using (var memoryStream = new MemoryStream())
                        using (var binaryWriter = new BinaryWriter(memoryStream))
                        {
                            fileType.Pack(binaryWriter);

                            buffer = memoryStream.ToArray();
                        }
                    }
                    catch (NotImplementedException)
                    {
                        // Pack is not implemented, just use the existing buffer

                        var datReader = datDatabase.GetReaderForFile(kvp.Key);

                        buffer = datReader.Buffer;
                    }
                }
                else
                {
                    // FileType is not cached

                    var datReader = datDatabase.GetReaderForFile(kvp.Key);

                    buffer = datReader.Buffer;
                }

                // Update the DatFile FileOffset
                kvp.Value.FileOffset = (uint)fileStream.Position;

                // Update the DatFile FileSize
                kvp.Value.FileSize = (uint)buffer.Length;

                datWriter.Writer(fileStream, datDatabase.Header.BlockSize, buffer);
            }


            // Rebuild our Root Directory

            // Clear the current root
            for (int i = 0; i < datDatabase.RootDirectory.DatDirectoryHeader.Branches.Length; i++)
                datDatabase.RootDirectory.DatDirectoryHeader.Branches[i] = 0xCDCDCDCD;
            datDatabase.RootDirectory.DatDirectoryHeader.Entries = new DatFile[0];
            datDatabase.RootDirectory.Directories.Clear();

            // Build a new sorted directory
            var sortedFiles = datDatabase.AllFiles.Values.OrderBy(r => r.ObjectId).ToList();

            foreach (var sortedFile in sortedFiles)
            {
                if (!AddFileToDirectory(datDatabase.RootDirectory, datDatabase.Header.BlockSize, sortedFile, 2))
                    throw new Exception();
            }

            // Write our Root Directory
            datDatabase.RootDirectory.Write(fileStream);

            // Write our Root Directory again, this time it will have the correct Branches tables
            fileStream.Position = datDatabase.RootDirectory.RootSectorOffset;

            datDatabase.RootDirectory.Write(fileStream);


            // Write our header
            datDatabase.Header.FileSize = (uint)fileStream.Position;

            datDatabase.Header.FreeHead = 0;
            datDatabase.Header.FreeTail = 0;
            datDatabase.Header.FreeCount = 0;
            datDatabase.Header.BTree = datDatabase.RootDirectory.RootSectorOffset;

            fileStream.Seek(DatDatabase.DAT_HEADER_OFFSET, SeekOrigin.Begin);
            using (var writer = new BinaryWriter(fileStream, Encoding.Default, true))
                datDatabase.Header.Pack(writer);


            // Write the header at 0x100, no clue what this is
            // the 64 bytes @ 0x100 is part of the iteration tracking
            fileStream.Seek(0x100, SeekOrigin.Begin);
            if (datDatabase is PortalDatDatabase)
                fileStream.Write(new byte[] { 0x00, 0x50, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xFF, 0xFF, 0x00, 0x2C, 0xCE, 0x0F, 0x0C, 0x00, 0x00, 0x00, 0x39, 0x4E, 0x7B, 0x55, 0x01, 0x00, 0x00, 0x00, 0x00, 0x64, 0x38, 0x37, 0x00, 0x00, 0x00, 0x00, 0x2B, 0x00, 0x00, 0x00, 0x00, 0x1C, 0x38, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 0x40);
            else
                throw new NotImplementedException();


            fileStream.Close();
        }

        private static bool AddFileToDirectory(DatDirectory datDirectory, uint blockSize, DatFile datFile, int depth)
        {
            if (depth > 0)
            {
                DatDirectory lastDirectory;

                if (datDirectory.Directories.Count == 0)
                {
                    lastDirectory = new DatDirectory(0, blockSize);

                    datDirectory.Directories.Add(lastDirectory);
                }
                else
                    lastDirectory = datDirectory.Directories.Last();

                if (AddFileToDirectory(lastDirectory, blockSize, datFile, depth - 1))
                    return true;
            }

            if (AddFileToHeader(datDirectory.DatDirectoryHeader, datFile))
            {
                if (depth > 0)
                {
                    var sub = new DatDirectory(0, blockSize);

                    datDirectory.Directories.Add(sub);
                }

                return true;
            }

            return false;
        }

        private static bool AddFileToHeader(DatDirectoryHeader datDirectoryHeader, DatFile datFile)
        {
            if (datDirectoryHeader.Entries == null)
                datDirectoryHeader.Entries = new DatFile[0];

            // Values of 0x2B - 0x30 will work.
            // Values lower run out of dictionary room.
            // Values higher and the client won't load the dat
            if (datDirectoryHeader.Entries.Length >= 0x2D)
                return false;

            var entries = new List<DatFile>(datDirectoryHeader.Entries);

            entries.Add(datFile);

            datDirectoryHeader.Entries = entries.ToArray();

            return true;
        }
    }
}
