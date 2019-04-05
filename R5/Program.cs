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
                    foreach (var F in Directory.EnumerateFiles(DirName))
                    {
                        var H = GetHeader(F);
                        if (H != null)
                        {
                            FileList.Add(F, H);
                        }
                    }
                    if (FileList.Count == 0)
                    {
                        Error.WriteLine("No files found with a valid header");
                        return RET.NOT_FOUND;
                    }
                    if (!FileList.All(m => m.Value.Id == FileList.First().Value.Id))
                    {
                        Error.WriteLine("Multiple different headers found in the given directory. Please specify one file as input");
                        return RET.AMBIGUOUS;
                    }
                }
                else
                {
                    var TestHeader = GetHeader(Path.Combine(DirName, SourceFile));
                    if (TestHeader == null)
                    {
                        Error.WriteLine("The file contains no valid header");
                        return RET.NO_HEADER;
                    }
                    //Get all matching headers
                    foreach (var F in Directory.EnumerateFiles(DirName))
                    {
                        var H = GetHeader(F);
                        if (H != null && H.Id == TestHeader.Id)
                        {
                            FileList.Add(F, H);
                        }
                    }
                }
                if (!ValidateHeaders(FileList.Select(m => m.Value).ToArray()))
                {
                    Error.WriteLine("Too many corrupt headers");
                    return RET.INVALID_HEADER;
                }

                var HeaderList = FileList.Select(m => m.Value).OrderBy(m => m.PartNumber).ToArray();

                //Recover damaged part if needed
                if (!HasAllParts(HeaderList))
                {
                    if (!CanRecover(HeaderList))
                    {
                        Error.WriteLine("Unable to recover. Can only recover one part, need CRC for recovery");
                        return RET.CANT_RECOVER;
                    }
                    BitArray BA = null;
                    foreach (var F in FileList)
                    {
                        using (var FS = File.OpenRead(F.Key))
                        {
                            GetHeader(FS);
                            byte[] Data = new byte[FS.Length - FS.Position];
                            FS.Read(Data, 0, Data.Length);
                            if (BA == null)
                            {
                                BA = new BitArray(Data);
                            }
                            else
                            {
                                BA.Xor(new BitArray(Data));
                            }
                        }
                    }
                }

                if (HasAllParts(HeaderList))
                {
                    //All parts here
                    var First = FileList.First(m => m.Value.PartNumber == 1).Value;
                    using (var FS = File.Create(FileName))
                    {
                        for (var i = 1; i <= First.PartCount; i++)
                        {
                            var CurrentHeader = FileList.First(m => m.Value.PartNumber == i);
                            using (var IN = File.OpenRead(CurrentHeader.Key))
                            {
                                //Discard header
                                Header.FromStream(IN);
                                IN.CopyTo(FS);
                            }
                        }
                        FS.Flush();
                        FS.Position = First.FileSize;
                        FS.SetLength(First.FileSize);
                    }
                }
            }
            else
            {
                Error.WriteLine("Directory not found: {0}", DirName);
                return RET.NOT_FOUND;
            }
            return RET.SUCCESS;
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
            return H.All(m => m.FileName == H[0].FileName) &&
                H.All(m => m.FileSize == H[0].FileSize) &&
                H.All(m => m.Id == H[0].Id) &&
                H.All(m => m.PartCount == H[0].PartCount) &&
                H.All(m => m.Encrypted == H[0].Encrypted);
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
                    Error.WriteLine("Invalid number of parts, expecting 1-999 but got {0}", PartCount);
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
