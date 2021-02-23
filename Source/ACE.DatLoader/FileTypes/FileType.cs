using System.IO;

namespace ACE.DatLoader.FileTypes
{
    public abstract class FileType : IUnpackable, IPackable
    {
        public uint Id { get; protected set; }

        public abstract void Unpack(BinaryReader reader);

        public virtual void Pack(BinaryWriter writer)
        {
            throw new System.NotImplementedException();
        }
    }
}
