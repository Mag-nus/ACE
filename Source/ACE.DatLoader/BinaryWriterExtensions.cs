using System;
using System.IO;
using System.Numerics;

namespace ACE.DatLoader
{
    static class BinaryWriterExtensions
    {
        /// <summary>
        /// Aligns the stream to the next DWORD boundary.
        /// </summary>
        public static void AlignBoundary(this BinaryWriter writer)
        {
            // Aligns the DatReader to the next DWORD boundary.
            long alignDelta = writer.BaseStream.Length % 4;

            if (alignDelta > 0)
                for (var i = 0; i < (4 - alignDelta); i++)
                    writer.Write((byte)0);
        }


        /// <summary>
        /// A Compressed UInt32 can be 1, 2, or 4 bytes.<para />
        /// If the first MSB (0x80) is 0, it is one byte.<para />
        /// If the first MSB (0x80) is set and the second MSB (0x40) is 0, it's 2 bytes.<para />
        /// If both (0x80) and (0x40) are set, it's 4 bytes.
        /// 
        /// Thanks to GDLE for this code!
        /// </summary>
        public static void WriteCompressedUInt32(this BinaryWriter writer, uint value)
        {
            int iValue = (int)value;
            if (value <= 0x7F)
                writer.Write((byte)iValue);
            else if (value <= 0x3FFF)
            {
                byte[] shortBytes = BitConverter.GetBytes((ushort)iValue);
                byte b0 = (byte)(shortBytes[1] | 0x80);
                byte b1 = (byte)shortBytes[0];
                writer.Write(b0);
                writer.Write(b1);
            }
            else
            {
                byte[] intBytes = BitConverter.GetBytes(value);
                byte b0 = (byte)(intBytes[3] | 0xC0);
                byte b1 = (byte)intBytes[2];
                byte b2 = (byte)intBytes[0];
                byte b3 = (byte)intBytes[1];

                writer.Write(b0);
                writer.Write((byte)intBytes[2]);
                writer.Write((byte)intBytes[0]);
                writer.Write((byte)intBytes[1]);
            }
        }

        /// <summary>
        /// Writes a string as defined by the first sizeOfLength-byte's length
        /// </summary>
        public static void WritePString(this BinaryWriter writer, string theString, uint sizeOfLength = 2)
        {
            int stringlength = theString.Length;
            switch (sizeOfLength)
            {
                case 1:
                    writer.Write((byte)stringlength);
                    break;
                case 2:
                default:
                    writer.Write((short)stringlength);
                    break;
            }

            writer.Write(System.Text.Encoding.GetEncoding(1252).GetBytes(theString));
        }

        /// <summary>
        /// Writes a string as defined by the first byte's length and removes the obfuscation (upper/lower nibbles swapped)
        /// </summary>
        public static void WriteObfuscatedString(this BinaryWriter writer, string theString)
        {
            writer.Write((ushort)theString.Length);

            byte[] myBytes = System.Text.Encoding.Default.GetBytes(theString);

            for (var i = 0; i < theString.Length; i++)
                // flip the bytes in the string to undo the obfuscation: i.e. 0xAB => 0xBA
                myBytes[i] = (byte)((myBytes[i] >> 4) | (myBytes[i] << 4));

            writer.Write(myBytes);
        }

        public static void WriteUnicodeString(this BinaryWriter writer, string value)
        {
            writer.WriteCompressedUInt32((uint)value.Length);
            for (int i = 0; i < value.Length; i++)
                writer.Write((ushort)value[i]);
        }

        /// <summary>
        /// Writes a Vector3 object read out as 3 floats, x y z
        /// </summary>
        public static void Write(this BinaryWriter writer, Vector3 vector)
        {
            writer.Write(vector.X);
            writer.Write(vector.Y);
            writer.Write(vector.Z);
        }
    }
}
