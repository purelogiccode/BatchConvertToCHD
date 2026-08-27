using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

/// <summary>
/// Builds synthetic PBP files in memory for unit testing PBPSharp.
/// Creates valid PBP structure: header + SFO + PSAR (with TOC, index, and compressed/uncompressed ISO blocks).
/// </summary>
internal sealed class PbpTestFileBuilder
{
    private const int IsoBlockSize = 0x930; // 2352 bytes per sector
    private const int BlockSize = 16 * IsoBlockSize; // 37632 bytes per block
    private const uint PsarIsoOffset = 0x100000;
    private const uint PsarIndexOffset = 0x4000;
    private const uint PsarTocOffset = 0x800;
    private const uint PsarGameIdOffset = 0x400;

    private string _title = "Test Game";
    private string _discId = "SLUS00001";
    private string _category = "ME";
    private int _blockCount = 2;
    private bool _compressBlocks = true;
    private bool _multiDisc;
    private List<int>? _multiDiscPositions;
    private byte[]? _customIsoBlock1Data;

    public PbpTestFileBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public PbpTestFileBuilder WithDiscId(string discId)
    {
        _discId = discId;
        return this;
    }

    public PbpTestFileBuilder WithCategory(string category)
    {
        _category = category;
        return this;
    }

    public PbpTestFileBuilder WithBlockCount(int count)
    {
        _blockCount = count;
        return this;
    }

    public PbpTestFileBuilder WithCompressedBlocks(bool compress)
    {
        _compressBlocks = compress;
        return this;
    }

    public PbpTestFileBuilder AsMultiDisc(params int[] positions)
    {
        _multiDisc = true;
        _multiDiscPositions = positions?.ToList();
        return this;
    }

    public PbpTestFileBuilder WithCustomIsoBlock1Data(byte[] data)
    {
        _customIsoBlock1Data = data;
        return this;
    }

    /// <summary>
    /// Builds the PBP file and writes it to the specified path.
    /// </summary>
    public void BuildTo(string path)
    {
        File.WriteAllBytes(path, Build());
    }

    /// <summary>
    /// Builds the PBP file and returns it as a byte array.
    /// </summary>
    public byte[] Build()
    {
        using var ms = new MemoryStream();
        BuildTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds the PBP file and writes it to the specified stream.
    /// </summary>
    public void BuildTo(Stream stream)
    {
        var sfoBytes = BuildSfo(_title, _category, _discId);
        const int sfoOffset = 0x28;
        var dataPsarOffset = sfoOffset + sfoBytes.Length;
        // Align PSAR to 0x100 boundary
        dataPsarOffset = (dataPsarOffset + 0xFF) & ~0xFF;

        // PBP Header
        WritePbpHeader(stream, sfoOffset, dataPsarOffset);

        // SFO
        stream.Write(sfoBytes, 0, sfoBytes.Length);

        // Pad to PSAR offset
        PadTo(stream, dataPsarOffset);

        if (_multiDisc)
        {
            WriteMultiDiscPsar(stream, dataPsarOffset);
        }
        else
        {
            WriteSingleDiscPsar(stream, dataPsarOffset);
        }
    }

    private static void WritePbpHeader(Stream stream, int sfoOffset, int dataPsarOffset)
    {
        Span<byte> header = stackalloc byte[PbpHeader.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], PbpHeader.MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], 1u); // version
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], sfoOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], 0x100); // icon0
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], 0x100); // icon1
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], 0x100); // pic0
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], 0x100); // pic1
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], 0x100); // snd0
        BinaryPrimitives.WriteInt32LittleEndian(header[32..36], 0x100); // dataPsp
        BinaryPrimitives.WriteInt32LittleEndian(header[36..40], dataPsarOffset); // dataPsar
        stream.Write(header);
    }

    private void WriteSingleDiscPsar(Stream stream, int psarOffset)
    {
        // "PSISOIMG0000" header
        stream.Write("PSISOIMG0000"u8);
        PadTo(stream, psarOffset + (int)PsarGameIdOffset);

        // Game ID at PSAR+0x400
        WriteGameId(stream, _discId);

        // TOC at PSAR+0x800
        PadTo(stream, psarOffset + (int)PsarTocOffset);
        WriteMinimalToc(stream);

        // ISO index at PSAR+0x4000
        PadTo(stream, psarOffset + (int)PsarIndexOffset);
        var isoDataOffset = WriteIsoIndex(stream, _blockCount);

        // ISO data at PSAR+0x100000
        PadTo(stream, psarOffset + (int)PsarIsoOffset);
        WriteIsoData(stream, _blockCount, isoDataOffset);
    }

    private void WriteMultiDiscPsar(Stream stream, int psarOffset)
    {
        // "PSTITLEIMG000000" header
        stream.Write("PSTITLEIMG000000"u8);

        // 8 bytes padding (2 x uint32 zeros)
        stream.Write(new byte[8]);

        // Magic DWORDs
        Span<byte> magic = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(magic[0..4], 0x2CC9C5BCu);
        BinaryPrimitives.WriteUInt32LittleEndian(magic[4..8], 0x33B5A90Fu);
        BinaryPrimitives.WriteUInt32LittleEndian(magic[8..12], 0x06F6B4B3u);
        BinaryPrimitives.WriteUInt32LittleEndian(magic[12..16], 0xB25945BAu);
        stream.Write(magic);

        // 0x76 uint32 zeros
        for (var i = 0; i < 0x76; i++)
            stream.Write("\0\0\0\0"u8);

        // 5 disc position uint32s
        var positions = _multiDiscPositions ?? [0x200000];
        Span<byte> posBytes = stackalloc byte[20];
        for (var i = 0; i < 5; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                posBytes[(i * 4)..],
                i < positions.Count ? (uint)positions[i] : 0u
            );
        }

        stream.Write(posBytes);

        // Write disc data at each non-zero position
        for (var i = 0; i < positions.Count; i++)
        {
            var discOffset = psarOffset + positions[i];
            PadTo(stream, discOffset);
            WriteDiscData(stream, discOffset, _discId);
        }
    }

    private void WriteDiscData(Stream stream, int discPsarOffset, string discId)
    {
        // Game ID at PSAR+0x400
        PadTo(stream, discPsarOffset + (int)PsarGameIdOffset);
        WriteGameId(stream, discId);

        // TOC at PSAR+0x800
        PadTo(stream, discPsarOffset + (int)PsarTocOffset);
        WriteMinimalToc(stream);

        // ISO index at PSAR+0x4000
        PadTo(stream, discPsarOffset + (int)PsarIndexOffset);
        var isoDataOffset = WriteIsoIndex(stream, _blockCount);

        // ISO data at PSAR+0x100000
        PadTo(stream, discPsarOffset + (int)PsarIsoOffset);
        WriteIsoData(stream, _blockCount, isoDataOffset);
    }

    private static void WriteGameId(Stream stream, string discId)
    {
        // Game ID format: null byte, 4 chars, null byte, 5 chars = 11 bytes total
        // e.g. "\0SLUS\000001" -> "SLUS00001"
        var padded = discId.PadRight(9);
        stream.WriteByte(0);
        stream.Write(Encoding.ASCII.GetBytes(padded[..4]));
        stream.WriteByte(0);
        stream.Write(Encoding.ASCII.GetBytes(padded[4..9]));
    }

    private static void WriteMinimalToc(Stream stream)
    {
        // A0 point: first track is 1
        WriteEntry(0x41, 0xA0, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00);

        // A1 point: last track is 1
        WriteEntry(0x41, 0xA1, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00);

        // A2 point: lead-out at MSF 00:02:00
        WriteEntry(0x41, 0xA2, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00);

        // Track 1 (data) starting at MSF 00:02:00
        WriteEntry(0x41, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00);
        return;

        // Each TOC entry is 10 bytes, matching the layout read by PbpDiscInfo/ReadTOC:
        //   [0]     track type (0x41 data / 0x01 audio)
        //   [2]     point (BCD track number, or control point 0xA0/0xA1/0xA2)
        //   [3..5]  MSF address (binary-coded decimal minutes, seconds, frames)
        //   [7]     A0/A1 only: BCD first/last track number (see reference TOCHelper usage)
        //   [7..9]  A2 only: MSF of the lead-out
        void WriteEntry(byte type, byte point, byte m, byte s, byte f, byte b7, byte b8, byte b9)
        {
            stream.WriteByte(type);
            stream.WriteByte(0x00);
            stream.WriteByte(point);
            stream.WriteByte(m);
            stream.WriteByte(s);
            stream.WriteByte(f);
            stream.WriteByte(0x00);
            stream.WriteByte(b7);
            stream.WriteByte(b8);
            stream.WriteByte(b9);
        }
    }

    private List<uint> WriteIsoIndex(Stream stream, int blockCount)
    {
        var offsets = new List<uint>();
        uint currentOffset = 0;
        Span<byte> entry = stackalloc byte[32];

        for (var i = 0; i < blockCount; i++)
        {
            offsets.Add(currentOffset);

            var blockData = GetBlockData(i);
            var compressedLength = _compressBlocks ? CompressBlock(blockData).Length : BlockSize;

            // Write index entry (32 bytes)
            entry.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0..4], currentOffset);
            BinaryPrimitives.WriteInt32LittleEndian(
                entry[4..8],
                _compressBlocks ? compressedLength : BlockSize
            );
            stream.Write(entry);

            currentOffset += (uint)(_compressBlocks ? compressedLength : BlockSize);
        }

        return offsets;
    }

    // ReSharper disable once UnusedParameter.Local
    private void WriteIsoData(Stream stream, int blockCount, List<uint> offsets)
    {
        for (var i = 0; i < blockCount; i++)
        {
            var blockData = GetBlockData(i);

            if (_compressBlocks)
            {
                var compressed = CompressBlock(blockData);
                stream.Write(compressed);
            }
            else
            {
                stream.Write(blockData);
            }
        }
    }

    private byte[] GetBlockData(int blockIndex)
    {
        if (blockIndex == 1)
        {
            // Block index 1 (the second block) carries the ISO9660 volume information:
            // PbpDiscInfo.ReadIsoSize reads the sector count from bytes 104..107 of this
            // block, exactly like the reference implementation's GetIsoSize().
            var data = new byte[BlockSize];

            // Fill with a recognizable pattern
            for (var i = 0; i < BlockSize; i++)
                data[i] = (byte)((i + blockIndex) & 0xFF);

            if (_customIsoBlock1Data != null && _customIsoBlock1Data.Length <= BlockSize)
            {
                Buffer.BlockCopy(_customIsoBlock1Data, 0, data, 0, _customIsoBlock1Data.Length);
            }
            else
            {
                // ISO size in sectors at bytes 104-107 (little-endian uint32)
                // Total ISO size = sectorCount * IsoBlockSize
                // We want _blockCount blocks, each with 16 sectors
                var sectorCount = (uint)(_blockCount * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(104, 4), sectorCount);
            }

            return data;
        }

        // Other blocks: fill with pattern
        var block = new byte[BlockSize];
        for (var i = 0; i < BlockSize; i++)
            block[i] = (byte)((i + blockIndex * 17) & 0xFF);
        return block;
    }

    private static byte[] CompressBlock(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    private static byte[] BuildSfo(string title, string category, string discId)
    {
        using var ms = new MemoryStream();

        var entries = new List<(string Key, ushort Format, byte[] Data)>
        {
            ("TITLE", 0x0204, Encoding.UTF8.GetBytes(title)),
            ("CATEGORY", 0x0204, Encoding.UTF8.GetBytes(category)),
            ("DISC_ID", 0x0204, Encoding.UTF8.GetBytes(discId)),
            ("BOOTABLE", 0x0404, BitConverter.GetBytes(1u)),
            ("REGION", 0x0404, BitConverter.GetBytes(0xFFFFFFFFu)),
        };

        // SFO header placeholder (20 bytes)
        ms.Write(new byte[20]);

        // Directory entries
        var keyTable = new MemoryStream();
        var dataTable = new MemoryStream();
        var dirEntries = new List<byte[]>();

        foreach (var (key, format, data) in entries)
        {
            var keyOffset = (ushort)keyTable.Position;
            var dataOffset = (uint)dataTable.Position;

            keyTable.Write(Encoding.ASCII.GetBytes(key));
            keyTable.WriteByte(0);

            dataTable.Write(data);
            // Pad data to maxLength
            var maxLength = (uint)Math.Max(data.Length, 32);
            while (dataTable.Length < dataOffset + maxLength)
                dataTable.WriteByte(0);

            var dirEntry = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(dirEntry.AsSpan(0, 2), keyOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(dirEntry.AsSpan(2, 2), format);
            BinaryPrimitives.WriteUInt32LittleEndian(dirEntry.AsSpan(4, 4), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dirEntry.AsSpan(8, 4), maxLength);
            BinaryPrimitives.WriteUInt32LittleEndian(dirEntry.AsSpan(12, 4), dataOffset);
            dirEntries.Add(dirEntry);
        }

        foreach (var dirEntry in dirEntries)
            ms.Write(dirEntry);

        var keyTableOffset = (uint)(20 + entries.Count * 16);
        var dataTableOffset = (uint)(keyTableOffset + keyTable.Length);

        ms.Write(keyTable.ToArray());
        ms.Write(dataTable.ToArray());

        // Patch header
        var sfoBytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(sfoBytes.AsSpan(0, 4), 0x46535000u); // magic
        BinaryPrimitives.WriteUInt32LittleEndian(sfoBytes.AsSpan(4, 4), 0x00000101u); // version
        BinaryPrimitives.WriteUInt32LittleEndian(sfoBytes.AsSpan(8, 4), keyTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(sfoBytes.AsSpan(12, 4), dataTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(sfoBytes.AsSpan(16, 4), (uint)entries.Count);

        return sfoBytes;
    }

    private static void PadTo(Stream stream, int offset)
    {
        while (stream.Position < offset)
            stream.WriteByte(0);
    }
}