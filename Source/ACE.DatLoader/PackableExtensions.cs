using System.Collections.Generic;
using System.IO;


namespace ACE.DatLoader
{
    static class PackableExtensions
    {
        /// <summary>
        /// A SmartArray uses a Compressed UInt32 for the length.
        /// </summary>
        public static void PackSmartArray(this List<int> value, BinaryWriter writer)
        {
            writer.Write((uint)value.Count);
            for (int i = 0; i < value.Count; i++)
                writer.Write(value[i]);
        }

        /// <summary>
        /// A SmartArray uses a Compressed UInt32 for the length.
        /// </summary>
        public static void PackSmartArray(this List<uint> value, BinaryWriter writer)
        {
            writer.WriteCompressedUInt32((uint)value.Count);
            for (int i = 0; i < value.Count; i++)
                writer.Write(value[i]);
        }

        /// <summary>
        /// A SmartArray uses a Compressed UInt32 for the length.
        /// </summary>
        public static void PackSmartArray<T>(this List<T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.WriteCompressedUInt32((uint)value.Count);

            for (int i = 0; i < value.Count; i++)
                value[i].Pack(writer);
        }


        /// <summary>
        /// A SmartArray uses a Compressed UInt32 for the length.
        /// </summary>
        public static void PackSmartArray<T>(this Dictionary<ushort, T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.WriteCompressedUInt32((uint)value.Count);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        /// <summary>
        /// A SmartArray uses a Compressed UInt32 for the length.
        /// </summary>
        public static void PackSmartArray<T>(this Dictionary<int, T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.WriteCompressedUInt32((uint)value.Count);

            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        /// <summary>
        /// A SmartArray uses a Compressed UInt32 for the length.
        /// </summary>
        public static void PackSmartArray<T>(this Dictionary<uint, T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.WriteCompressedUInt32((uint)value.Count);

            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }


        /// <summary>
        /// A PackedHashTable uses a UInt16 for length, and a UInt16 for bucket size.
        /// We don't need to worry about the bucket size with C#.
        /// </summary>
        public static void PackHashTable(this Dictionary<uint, uint> value, BinaryWriter writer, ushort bucketSize)
        {
            writer.Write((ushort)value.Count);
            writer.Write(bucketSize);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                writer.Write(e.Value);
            }
        }

        /// <summary>
        /// A PackedHashTable uses a UInt16 for length, and a UInt16 for bucket size.
        /// We don't need to worry about the bucket size with C#.
        /// </summary>
        public static void PackHashTable<T>(this Dictionary<uint, T> value, BinaryWriter writer, ushort bucketSize) where T : IPackable, new()
        {
            writer.Write((ushort)value.Count);
            writer.Write(bucketSize);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        /// <summary>
        /// A PackedHashTable uses a UInt16 for length, and a UInt16 for bucket size.
        /// We don't need to worry about the bucket size with C#.
        /// </summary>
        public static void PackHashTable<T>(this SortedDictionary<uint, T> value, BinaryWriter writer, ushort bucketSize) where T : IPackable, new()
        {
            writer.Write((ushort)value.Count);
            writer.Write(bucketSize);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        /// <summary>
        /// A list that uses a Int32 for the length.
        /// </summary>
        public static void Pack(this List<uint> value, BinaryWriter writer)
        {
            writer.Write(value.Count);

            for (int i = 0; i < value.Count; i++)
                writer.Write(value[i]);
        }

        /// <summary>
        /// A list that uses a UInt32 for the length.
        /// </summary>
        public static void Pack<T>(this List<T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.Write(value.Count);
            for (int i = 0; i < value.Count; i++)
                value[i].Pack(writer);
        }

        /// <summary>
        /// A Dictionary that uses a Int32 for the length.
        /// </summary>
        public static void Pack<T>(this Dictionary<ushort, T> value, BinaryWriter writer) where T : IPackable, new()
        {
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        /// <summary>
        /// A Dictionary that uses a Int32 for the length.
        /// </summary>
        public static void Pack<T>(this Dictionary<int, T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.Write(value.Count);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        public static void Pack<T>(this Dictionary<uint, T> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.Write((uint)value.Count);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }

        /// <summary>
        /// A Dictionary that uses a Int32 for the length.
        /// </summary>
        public static void Pack<T>(this Dictionary<uint, Dictionary<uint, T>> value, BinaryWriter writer) where T : IPackable, new()
        {
            writer.Write(value.Count);
            foreach (var e in value)
            {
                writer.Write(e.Key);
                e.Value.Pack(writer);
            }
        }
    }
}
