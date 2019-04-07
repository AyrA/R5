using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static System.Console;

namespace R5
{
    class Program
    {
        private struct RET
        {
            public const int SUCCESS = 0;
            public const int NOT_FOUND = 1;
            public const int DIR_CREATE_ERROR = 2;
            public const int INVALID_PART_COUNT = 3;
            public const int ARG_COUNT = 4;
            public const int AMBIGUOUS = 5;
            public const int NO_HEADER = 6;
            public const int INVALID_HEADER = 7;
            public const int CANT_RECOVER = 8;
            public const int HELP = 255;
        }

        static int Main(string[] args)
        {
            var DirName = string.Empty;
            var FileName = string.Empty;

#if DEBUG
            args = new string[]
            {
                @"C:\Temp\R5",
                @"C:\Temp\R5\Dyson Sphere-547495626.mp3",
            };
#endif

            if (args.Length < 2 || args.Contains("/?"))
            {
                return Help();
            }
            if (args.Length == 3)
            {
                FileName = args[1];
                DirName = args[2];
                int PartCount = 0;
                if (int.TryParse(args[0], out PartCount))
                {
                    return Split(FileName, DirName, PartCount);
                }
                Error.WriteLine("{0} is not a valid number", args[0]);
                return RET.INVALID_PART_COUNT;
            }
            else if (args.Length == 2)
            {
                DirName = args[0];
                FileName = args[1];
                return Join(DirName, FileName);
            }
            Error.WriteLine("Invalid number of arguments");
            return RET.ARG_COUNT;
        }

        private static int Join(string DirName, string FileName)
        {
            string SourceFile = null;
            //Check if directory name is a single file and adjust values as needed
            if (File.Exists(DirName))
            {
                SourceFile = Path.GetFileName(DirName);
                DirName = Path.GetDirectoryName(DirName);
            }
            if (Directory.Exists(DirName))
            {
                var FileList = new Dictionary<string, Header>();
                if (SourceFile == null)
                {
                    Error.WriteLine("Header Collect: Auto detecting files with headers");
                    foreach (var F in Directory.EnumerateFiles(DirName))
                    {
                        var H = GetHeader(F);
                        if (H != null)
                        {
                            Error.WriteLine("Header Collect: Detected header in {0}", Path.GetFileName(F));
                            FileList.Add(F, H);
                        }
                    }
                    if (FileList.Count == 0)
                    {
                        Error.WriteLine("Header Collect: No files found with a valid header");
                        return RET.NOT_FOUND;
                    }
                    if (!FileList.All(m => m.Value.Id == FileList.First().Value.Id))
                    {
                        Error.WriteLine("Header Collect: Multiple different headers found in the given directory. Please specify one file as input");
                        return RET.AMBIGUOUS;
                    }
                }
                else
                {
                    var TestHeader = GetHeader(Path.Combine(DirName, SourceFile));
                    if (TestHeader == null)
                    {
                        Error.WriteLine("Header Collect: The file contains no valid header");
                        return RET.NO_HEADER;
                    }
                    //Get all matching headers
                    foreach (var F in Directory.EnumerateFiles(DirName))
                    {
                        var H = GetHeader(F);
                        if (H != null)
                        {
                            if (H != null && H.Id == TestHeader.Id)
                            {
                                Error.WriteLine("Header Collect: Adding Header ", TestHeader.PartNumber);
                                FileList.Add(F, H);
                            }
                            else
                            {
                                Error.WriteLine("Header Collect: Ignoring header {0}", TestHeader.Id);
                            }
                        }
                    }
                }
                //TODO: Remove duplicate headers in case the user accidentally copied some header twice
                if (!ValidateHeaders(FileList.Select(m => m.Value).ToArray()))
                {
                    Error.WriteLine("Header Check: Detected duplicate/corrupt headers");
                    return RET.INVALID_HEADER;
                }

                var HeaderList = FileList.Select(m => m.Value).OrderBy(m => m.PartNumber).ToArray();

                //Recover damaged part if needed
                if (!HasAllParts(HeaderList))
                {
                    Error.WriteLine("Join: File list has missing part");
                    if (!CanRecover(HeaderList))
                    {
                        Error.WriteLine("Join: Unable to recover. Can only recover one part, need CRC for recovery");
                        Error.WriteLine("Join: Make sure you either have all parts, or the CRC file with at most one missing part.");
                        return RET.CANT_RECOVER;
                    }
                    using (var FS = File.Create(FileName))
                    {
                        long PosMissing = 0;
                        var Missing = GetMissingId(HeaderList);
                        var CRC = FileList.First(m => m.Value.PartNumber == 0);
                        BitArray BA = new BitArray(GetFileContent(CRC.Key));
                        Error.WriteLine("Join: Missing part is {0}", Missing);
                        for (var i = 1; i <= CRC.Value.PartCount; i++)
                        {
                            var Current = FileList.FirstOrDefault(m => m.Value.PartNumber == i);

                            if (i == Missing)
                            {
                                Error.WriteLine("Join Part {0}: Writing dummy segment", i);
                                //Write placeholder
                                PosMissing = FS.Position;
                                FS.Write(new byte[BA.Length / 8], 0, BA.Length / 8);
                            }
                            else
                            {
                                Error.WriteLine("Join Part {0}: Writing file segment", i);
                                var Content = GetFileContent(Current.Key);
                                FS.Write(Content, 0, Content.Length);
                                //BitArray needs equal length arrays
                                //The missing bytes are set to zero, which is the same as when we split the file
                                if (Content.Length < BA.Length / 8)
                                {
                                    Array.Resize(ref Content, BA.Length / 8);
                                }
                                BA.Xor(new BitArray(Content));
                            }
                        }
                        Error.WriteLine("Join Part {0}: Writing recovered segment", Missing);
                        FS.Flush();
                        FS.Position = PosMissing;
                        byte[] Temp = new byte[BA.Length / 8];
                        BA.CopyTo(Temp, 0);
                        FS.Write(Temp, 0, Temp.Length);

                        //Trim file if needed (might be too large if the last part was the recovered one)
                        if (FS.Position > CRC.Value.FileSize)
                        {
                            FS.Flush();
                            FS.Position = CRC.Value.FileSize;
                            FS.SetLength(CRC.Value.FileSize);
                        }

                        try
                        {
                            Error.WriteLine("Part Generator: Trying to recreate part {0}", Missing);
                            var CH = CRC.Value;
                            CH.PartNumber = Missing;
                            var NewName = Path.Combine(Path.GetDirectoryName(CRC.Key), CH.FileName) + string.Format(".{0:000}", Missing);
                            if (File.Exists(NewName))
                            {
                                throw new IOException($"{NewName} already exists");
                            }
                            using (var REC = File.Create(NewName))
                            {
                                CH.Serialize(REC);
                                REC.Write(Temp, 0, Temp.Length);
                            }
                            Error.WriteLine("Part Generator: Recreated part {0}", Missing);
                        }
                        catch (Exception ex)
                        {
                            Error.WriteLine("Part Generator: Unable to recreate part {0}", Missing);
                            Error.WriteLine("Part Generator: {0}", ex.Message);
                        }
                    }
                }
                else
                {
                    var HasCRC = FileList.Any(m => m.Value.PartNumber == 0);
                    Error.WriteLine("Join: File has all parts. Recovering normally");
                    if (!HasCRC)
                    {
                        Error.WriteLine("Join: CRC missing, will generate again");
                    }
                    //All parts here
                    var First = FileList.First(m => m.Value.PartNumber == 1).Value;
                    using (var FS = File.Create(FileName))
                    {
                        BitArray BA = null;
                        for (var i = 1; i <= First.PartCount; i++)
                        {
                            Error.WriteLine("Join Part {0}: Writing file segment", i);
                            byte[] Data = GetFileContent(FileList.First(m => m.Value.PartNumber == i).Key);
                            FS.Write(Data, 0, Data.Length);
                            if (!HasCRC)
                            {
                                if (BA == null)
                                {
                                    BA = new BitArray(Data);
                                }
                                else
                                {
                                    if (Data.Length < BA.Length / 8)
                                    {
                                        Array.Resize(ref Data, BA.Length / 8);
                                    }
                                    BA.Xor(new BitArray(Data));
                                }
                            }
                        }
                        //Trim file if needed
                        if (FS.Position > First.FileSize)
                        {
                            FS.Flush();
                            FS.Position = First.FileSize;
                            FS.SetLength(First.FileSize);
                        }
                        //Recover CRC if needed
                        if (BA != null)
                        {

                            var H = HeaderList.First();
                            //Generate part name from existing Header
                            var NewName = Path.Combine(Path.GetDirectoryName(FileName), H.FileName) + ".crc";
                            H.PartNumber = 0;
                            try
                            {
                                Error.WriteLine("Part Generator: Trying to recreate CRC");
                                if (File.Exists(NewName))
                                {
                                    throw new IOException($"{NewName} already exists");
                                }
                                using (var CRC = File.Create(NewName))
                                {
                                    H.Serialize(CRC);
                                    byte[] Data = new byte[BA.Length / 8];
                                    BA.CopyTo(Data, 0);
                                    CRC.Write(Data, 0, Data.Length);
                                }
                                Error.WriteLine("Part Generator: Recreated CRC");
                            }
                            catch (Exception ex)
                            {
                                Error.WriteLine("Part Generator: Unable to recreate CRC");
                                Error.WriteLine("Part Generator: {0}", ex.Message);
                            }
                        }
                    }
                }
            }
            else
            {
                Error.WriteLine("Header Collect: Directory not found: {0}", DirName);
                return RET.NOT_FOUND;
            }
            return RET.SUCCESS;
        }

        private static byte[] GetFileContent(string FileName)
        {
            using (var FS = File.OpenRead(FileName))
            {
                if (GetHeader(FS) == null)
                {
                    throw new InvalidDataException($"{FileName} has no header");
                }
                using (var MS = new MemoryStream())
                {
                    FS.CopyTo(MS);
                    return MS.ToArray();
                }
            }
        }

        private static bool CanRecover(Header[] H)
        {
            return H != null && H.Length > 0 && H.Length == H[0].PartCount;
        }

        private static int GetMissingId(Header[] H)
        {
            if (HasAllParts(H) || CanRecover(H))
            {
                for (var i = 1; i <= H[0].PartCount; i++)
                {
                    if (!H.Any(m => m.PartNumber == i))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private static bool HasAllParts(Header[] H)
        {
            if (H == null || H.Length == 0)
            {
                return false;
            }
            for (var i = 1; i <= H[0].PartCount; i++)
            {
                if (!H.Any(m => m.PartNumber == i))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ValidateHeaders(Header[] H)
        {
            return
                //Currently not supported
                !H[0].Encrypted &&

                //Basic sanity checks
                H[0].FileSize > 0 && //You can't split zero size files, can't you?
                H[0].PartCount > 1 && //Splitting a file into 1 part is useless, so joining is too (CRC would be identical to file content)
                H[0].Id != Guid.Empty && //Nullguid is not allowed
                
                //Validate all fields that are the same across all parts
                H.All(m => m.FileName == H[0].FileName) &&
                H.All(m => m.FileSize == H[0].FileSize) &&
                H.All(m => m.Id == H[0].Id) &&
                H.All(m => m.PartCount == H[0].PartCount) &&
                H.All(m => m.Encrypted == H[0].Encrypted) &&
                
                //Make sure each part appears at most once
                H.All(m => H.Count(n => n.PartNumber == m.PartNumber) == 1) &&
                
                //Range check for part count (Valid = 0 <= PartNumber <= PartCount)
                H.All(m => m.PartNumber >= 0 && m.PartNumber <= m.PartCount);
        }

        private static int Split(string FileName, string DirName, int PartCount)
        {
            if (File.Exists(FileName))
            {
                FileName = Path.GetFullPath(FileName);
                var NewName = Path.GetFileName(FileName);

                if (!Directory.Exists(DirName))
                {
                    try
                    {
                        Directory.CreateDirectory(DirName);
                    }
                    catch (Exception ex)
                    {
                        Error.WriteLine("Unable to create directory. Reason: {0}", ex.Message);
                        return RET.DIR_CREATE_ERROR;
                    }
                }
                DirName = Path.GetFullPath(DirName);
                if (PartCount > 1 && PartCount < 1000)
                {
                    using (var FS = File.OpenRead(FileName))
                    {
                        if (FS.Length <= PartCount)
                        {
                            Error.WriteLine("Part count too large for this file. Count must be less than size in bytes");
                            return RET.INVALID_PART_COUNT;
                        }
                        long PartLength = (long)Math.Ceiling(FS.Length * 1.0 / PartCount);
                        if (PartLength > int.MaxValue)
                        {
                            throw new ArgumentOutOfRangeException("Segment length too long. Create more parts.");
                        }
                        var H = new Header()
                        {
                            Encrypted = false,
                            FileName = Path.GetFileName(FileName),
                            FileSize = FS.Length,
                            PartCount = PartCount
                        };
                        BitArray Parity = new BitArray(new byte[PartLength]);

                        for (var i = 1; i <= PartCount; i++)
                        {
                            H.PartNumber = i;
                            byte[] Data = new byte[(int)PartLength];
                            int Readed = FS.Read(Data, 0, Data.Length);
                            if (Readed > 0)
                            {
                                Parity.Xor(new BitArray(Data));
                                using (var Part = File.Create(Path.Combine(DirName, NewName + string.Format(".{0:000}", i))))
                                {
                                    H.Serialize(Part);
                                    Part.Write(Data, 0, Readed);
                                }
                            }
                            else
                            {
                                throw new IOException("Expected data but stream returned none");
                            }
                        }
                        //Write Parity
                        H.PartNumber = 0;
                        using (var Part = File.Create(Path.Combine(DirName, $"{NewName}.crc")))
                        {
                            byte[] Data = new byte[(int)PartLength];
                            Parity.CopyTo(Data, 0);
                            H.Serialize(Part);
                            Part.Write(Data, 0, Data.Length);
                        }
                    }
                }
                else
                {
                    Error.WriteLine("Invalid number of parts, expecting 2-999 but got {0}", PartCount);
                    return RET.INVALID_PART_COUNT;
                }
            }
            else
            {
                Error.WriteLine("File not found: {0}", FileName);
                return RET.NOT_FOUND;
            }
            return RET.SUCCESS;
        }

        private static Header GetHeader(string FileName)
        {
            try
            {
                using (var FS = File.OpenRead(FileName))
                {
                    return GetHeader(FS);
                }
            }
            catch
            {
                return null;
            }
        }

        private static Header GetHeader(Stream Input)
        {
            try
            {
                return Header.FromStream(Input);
            }
            catch
            {
                return null;
            }
        }

        private static int Help()
        {
            Error.WriteLine(@"R5 [parts] <input> <output>

parts   -  Number of parts (encoding only)
input   -  Source file (encoding), or directory (decoding)
output  -  Destination directory (encoding), or file (decoding)");
            return RET.HELP;
        }
    }
}
