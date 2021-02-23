using System;
using System.IO;

namespace ACE.DatLoader
{
    public class DatWriter
    {
        public void Writer(FileStream stream, uint blockSize, byte[] buffer)
        {
            // Dat "file" is broken up into sectors that are not neccessarily congruous. Next address is stored in first four bytes of each sector.
            uint nextAddress;

            int bufferOffset = 0;

            while (bufferOffset < buffer.Length)
            {
                if (buffer.Length - bufferOffset <= blockSize - 4)
                {
                    stream.Write(new byte[4], 0, 4);
                    stream.Write(buffer, bufferOffset, buffer.Length - bufferOffset);
                    bufferOffset += (buffer.Length - bufferOffset);
                }
                else
                {
                    nextAddress = Convert.ToUInt32(stream.Position + blockSize);
                    stream.Write(new byte[4] { (byte)nextAddress, (byte)(nextAddress >> 8), (byte)(nextAddress >> 16), (byte)(nextAddress >> 24) }, 0, 4);
                    stream.Write(buffer, bufferOffset, Convert.ToInt32(blockSize) - 4); // Write our sector from the buffer[]
                    bufferOffset += Convert.ToInt32(blockSize) - 4; // Adjust this so we know where in our buffer[] the next sector gets written from
                }
            }

            // Make sure we're on a Header.BlockSize boundary
            if (stream.Position % blockSize != 0)
                stream.Position += (stream.Position % blockSize);
        }
    }
}
