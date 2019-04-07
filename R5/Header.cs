using System;
using System.IO;
using System.Text;

namespace R5
{
    public class Header
    {
        /// <summary>
        /// Header magic value
        /// </summary>
        public const string MAGIC = "R5FILE";
        /// <summary>
        /// Maximum supported header number
        /// </summary>
        public const int VERSION = 1;

        /// <summary>
        /// NUmber of bytes in a GUID
        /// </summary>
        private const int GUID_LENGTH = 16;

        public string FileName { get; set; }
        public long FileSize { get; set; }
        public int PartCount { get; set; }
        public int PartNumber { get; set; }
        public bool Encrypted { get; set; }
        public Guid Id { get; private set; }

        public Header()
        {
            Id = Guid.NewGuid();
        }

        public byte[] Serialize()
        {
            if (FileName == null)
            {
                throw new InvalidOperationException("Unable to serialize null as file name");
            }
            if (FileSize < 1)
            {
                throw new InvalidOperationException("Unable to serialize 0 or less as file size");
            }
            //Part 0 is the XOR block
            if (PartNumber < 0)
            {
                throw new InvalidOperationException("Unable to serialize negative part numbers");
            }
            if (PartCount < 2)
            {
                throw new InvalidOperationException("Unable to serialize 1 or less as part count");
            }
            if (PartNumber > PartCount)
            {
                throw new InvalidOperationException("Unable to serialize out of range part number ");
            }
            if (Id == Guid.Empty)
            {
                throw new InvalidOperationException("Unable to serialize an empty id.");
            }

            using (var MS = new MemoryStream())
            {
                using (var BW = new BinaryWriter(MS, Encoding.UTF8))
                {
                    BW.Write(MAGIC);
                    BW.Write(VERSION);
                    BW.Write(Id.ToByteArray());
                    BW.Write(FileName);
                    BW.Write(FileSize);
                    BW.Write(PartNumber);
                    BW.Write(PartCount);
                    BW.Write(Encrypted);
                    BW.Flush();
                    return MS.ToArray();
                }
            }
        }

        public void Serialize(Stream S)
        {
            byte[] Data = Serialize();
            S.Write(Data, 0, Data.Length);
        }

        public static Header FromBytes(byte[] Data)
        {
            using (var MS = new MemoryStream(Data, false))
            {
                return FromStream(MS);
            }
        }

        public static Header FromStream(Stream S)
        {
            var H = new Header();
            using (var BR = new BinaryReader(S, Encoding.UTF8, true))
            {
                if (BR.ReadString() == MAGIC)
                {
                    var V = BR.ReadInt32();
                    if (V >= 1)
                    {
                        H.Id = new Guid(BR.ReadBytes(GUID_LENGTH));
                        H.FileName = BR.ReadString();
                        H.FileSize = BR.ReadInt64();
                        H.PartNumber = BR.ReadInt32();
                        H.PartCount = BR.ReadInt32();
                        H.Encrypted = BR.ReadBoolean();
                        if (H.FileSize < 1)
                        {
                            throw new InvalidDataException("Invalid file size");
                        }
                        if (H.PartNumber < 0 || H.FileSize <= H.PartNumber)
                        {
                            throw new InvalidDataException("Part number outside of allowed range");
                        }
                        if (H.PartNumber > H.PartCount)
                        {
                            throw new InvalidDataException("Decoded part count too small for decoded part number");
                        }
                        if (H.PartCount < 1 || H.PartCount > 999)
                        {
                            throw new InvalidDataException("Part count outside of allowed range");
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("Header version is not supported");
                    }
                }
                else
                {
                    throw new InvalidDataException("Invalid header");
                }
            }
            if (H.Id == Guid.Empty)
            {
                throw new InvalidDataException("Id can't be nullbytes only");
            }
            return H;
        }
    }
}
