# R5

R5 is a "RAID 5 like" file splitting tool.

# How R5 (and RAID 5) works

- N: Number of disks
- M: Number of parts
- P0: Parity
- P(0 < x < M+1): Part

To simplify it, a file is split into M=N-1 parts.
The parity is calculated by combining all parts using the XOR operation (`P0 = P1 XOR P2 XOR P3 XOR ... PM`).
This allows recovery of a single missing part.

RAID 5 distributes the parity across all disks to level out the wear and stress across all disks.

Note: With RAID 5 operating directly and transparently on disks,
it doesn't actually splits files but merely operates on fixed size blocks on the disks.

## Advantages

- Doesn't needs the CRC to assemble a file back into a single piece
- Can recover a file with any of the M parts being lost
- Can recreate any single part (including parity) if lost
- Parity is only `FileSize/M` bytes long (rounded up). More parts means less overhead

# Using R5

## Usage scenario

For this tool to work properly you should distribute the parts across different locations.
For example you can split a file into 3 parts, keep the parity to yourself,
and distribute the 3 pieces across 3 different cloud drives.

## Splitting a file

	R5 <PartCount> <Input> <Output>

- `PartCount` is the number of parts the file should be split into (without the parity)
- `Input` is the file that is to be split into parts
- `Output` is a directory to receive the file parts

This will split the file into evenly sized parts and write them into the output directory using an `x.nnn` naming scheme,
where `x` is the original name and `nnn` is the part number. The parity will have the extension `.crc`.

Any existing file in the output directory is overwritten if names collide.

You are free to rename some or all parts after applying R5.
You don't need to rename them back before assembling.

The file size does not needs to be perfectly divisible by the number of parts specified,
the last part will be shorter than the others if needed to accomondate for the rounding.
R5 works out the part size by dividing the file size by the number of parts,
then rounding that number up to the nearest integer.

R5 will add a header which increases the total part size slightly.
If exact byte sizes are important, you can precalculate the header size if you know the input file name.
See **Part Header** chapter below for details on the header fields.

## Joining a file

	R5 <Input> <Output>

- `Input`: File or directory to join
- `Output`: File to write joined parts into

You are free to specify a directory as input argument as long as the directory contains only the parts of a single R5 file.
If it contains multiple different part files,
you need to specify the full file name as input.
You can specify any part, including the parity.
Files without a valid header are ignored when R5 scans the files.

The output must be a full file name to write the joined parts to.
The tool will not accept just a directory name currently.
The file is overwritten if it exists.

R5 will automatically scan all files in the input directory to find all matching parts.
It will figure out of its own if part recovery is possible and necessary.

**Note:** R5 will check if most of the fields in the part headers line up.
It will not attempt to fix corrupted headers.
It will detect duplicate headers and abort.

### Recovery matrix

The table below shows when a file with 3 parts (1-3) and parity (P) can be joined and when not.
it also shows when recreation of a missing part is possible.

| Available parts | Can recover | Need parity | Part recreation |
|-----------------|-------------|-------------|-----------------|
| 1, 2, 3, P      | Yes         | No          | Not needed      |
| 1, 2, 3         | Yes         | No          | Yes             |
| 1, 2,    P      | Yes         | Yes         | Yes             |
| 1,    3, P      | Yes         | Yes         | Yes             |
|    2, 3, P      | Yes         | Yes         | Yes             |
| 1,       P      | No          |             | No              |
|    2,    P      | No          |             | No              |
|       3, P      | No          |             | No              |
| 1               | No          |             | No              |
|    2            | No          |             | No              |
|       3         | No          |             | No              |
|          P      | No          |             | No              |

### Part Recreation

When Joining a file that has a single missing part **or** missing parity,
R5 will automatically recreate the missing part/parity in the directory that holds the input files.
To do so, it will reconstruct the original file name of that part/parity.
If the name is already in use, recreation is skipped.

Part recreation is always done after the file has been fully recovered.
If part recreation fails for any reason, for example if the disk is full or write protected,
it will skip the part recreation entirely.

# Part Header

Each part is given a header that identifies it. The stricture is a raw byte stream of fields.
Field order:

	<Magic><Version><Id><FileName><FileSize><PartNumber><PartCount><Encrypted>

| Name       | Version | Type   | Size (Bytes) | Description                            |
|------------|---------|--------|--------------|----------------------------------------|
| Magic      | 1       | String | 7            | Always "R5FILE"                        |
| Version    | 1       | Int32  | 4            | Header version                         |
| Id         | 1       | UUID   | 16           | Identical across all parts             |
| FileName   | 1       | String | 1+           | Length prefixed string                 |
| FileSize   | 1       | Int64  | 8            | Number of bytes in original file       |
| PartNumber | 1       | Int32  | 4            | Part number                            |
| PartCount  | 1       | Int32  | 4            | Total number of parts (without parity) |
| Encrypted  | 1       | Bool   | 1            | 1=Encrypted, 0=Plain                   |

## Field Details

- All numbers are in little endian format unless otherwise specified.
- Strings are in UTF-8 encoding without BOM and without null terminator.
- Strings are length prefixed using [LEB128](https://en.wikipedia.org/wiki/LEB128), the prefix specifies the number of bytes, not characters.

### Magic

This field is constant, causing the length prefix always to be the same.
The byte sequence for this field is therefore always `06 52 35 46 49 4C 45`

### Version

This is always 1 as of now.
Larger numbers mean more fields are added to the end of header.

### Id

This Id is used to identify parts of the same file. It's the same across all part headers for a specific file
but must differ between individual files.
R5 will currently not catch this if you do this.
This will lead to problems if multiple identical ids for different files are in the same directory.
A Null UUID is not allowed (all 16 bytes set to 0) and any non-version 4 UUID is discouraged.

Note: A UUID is encoded using big endian byte order ([See RFC 4122, Page 6](https://www.ietf.org/rfc/rfc4122.txt))

This means the UUID `11223344-5566-7788-9900-AABBCCDDEEFF`
is represented in the header as `44-33-22-11-66-55-88-77-99-00-AA-BB-CC-DD-EE-FF`
because the first 3 segments are treated as integers and not individual bytes.

Please serialize them correctly.
When comparing headers the byte order technically doesn't matters,
as long your tool serializes all UUID in the same (possibly wrong) way.
Be aware that one of the bytes that is flipped is the one holding the UUID version number,
which could prevent some other applications from decoding the UUID because only version 1 to 4 are defined.

### FileName

This is the original file name without path segments (file name only, not full path).

### FileSize

Size of the original file.
This can be used to truncate the recovered file
if the file size is not perfectly divisible by the part count and the last part was padded in some way.

### PartNumber

This is the current part number with `1` being the first part.
`0` is used to signify that this part is the parity.

### PartCount

Total number of parts, without including the parity.

### Encrypted

This is set to `1` if the contents are encrypted.
This is currently not implemented and must be `0`.

# Encryption

There is no encryption built into R5, the header field has merely been added for the future.
As of now, you can just encrypt the file before splitting it up to achieve the same effect.

# TODO

- [ ] Add encryption support
- [ ] Recover original file name from header when joining
- [ ] Show missing parts to the user if reassembly is not possible
- [ ] When all parts are available, use parity to detect errors.
- [ ] Strip duplicate parts from header list when joining.