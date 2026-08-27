using System.Text;
using PBPSharp;
using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

public class PbpFileTests : IDisposable
{
    private readonly string _tempDir;

    public PbpFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PbpFileTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OpenNonExistentFileReturnsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "nonexistent.pbp");
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.FileNotFound, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenEmptyFileReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "empty.pbp");
        File.WriteAllBytes(path, []);
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithWrongMagicReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "wrongmagic.pbp");
        var data = new byte[100];
        BitConverter.GetBytes(0x12345678u).CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithShortHeaderReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "short.pbp");
        var data = new byte[20]; // less than HeaderSize (40)
        BitConverter.GetBytes(PbpHeader.MagicValue).CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithInvalidPsarReturnsInvalidPsarHeader()
    {
        var path = CreatePbpWithInvalidPsar();
        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidPsarHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenFileWithInvalidSfoMagicReturnsInvalidSfo()
    {
        var path = Path.Combine(_tempDir, $"badsfo_{Guid.NewGuid():N}.pbp");

        using var ms = new MemoryStream();
        WriteStandardPbpHeader(ms);

        var sfo = BuildMinimalSfo();
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(sfo, 0); // corrupt the SFO magic
        ms.Write(sfo);

        while (ms.Position < 0x200)
            ms.WriteByte(0);

        File.WriteAllBytes(path, ms.ToArray());

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidSfo, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenMultiDiscPbpWithInvalidHeaderMagicReturnsInvalidPsarHeader()
    {
        var path = Path.Combine(_tempDir, $"badmagic_{Guid.NewGuid():N}.pbp");

        using var ms = new MemoryStream();
        WriteStandardPbpHeader(ms);
        ms.Write(BuildMinimalSfo());

        while (ms.Position < 0x200)
            ms.WriteByte(0);

        ms.Write("PSTITLEIMG000000"u8.ToArray());
        ms.Write(new byte[8]); // 2 x padding uint32
        ms.Write(new byte[16]); // wrong magic DWORDs (all zero)

        File.WriteAllBytes(path, ms.ToArray());

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.InvalidPsarHeader, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenSingleDiscPbpWithNoIsoIndexReturnsTruncatedPsar()
    {
        var path = Path.Combine(_tempDir, $"noindex_{Guid.NewGuid():N}.pbp");

        using var ms = new MemoryStream();
        WriteStandardPbpHeader(ms);
        ms.Write(BuildMinimalSfo());

        while (ms.Position < 0x200)
            ms.WriteByte(0);

        // PSISOIMG0000 disc with GameID/TOC regions but no ISO index table.
        ms.Write("PSISOIMG0000"u8.ToArray());
        while (ms.Position < 0x200 + 0x800 + 0x20)
            ms.WriteByte(0);

        File.WriteAllBytes(path, ms.ToArray());

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.TruncatedPsar, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenSingleDiscPbpWithNegativeBlockLengthReturnsCorruptFile()
    {
        var path = Path.Combine(_tempDir, $"negativelen_{Guid.NewGuid():N}.pbp");

        using var ms = new MemoryStream();
        WriteStandardPbpHeader(ms);
        ms.Write(BuildMinimalSfo());

        while (ms.Position < 0x200)
            ms.WriteByte(0);

        ms.Write("PSISOIMG0000"u8.ToArray());

        while (ms.Position < 0x200 + 0x4000)
            ms.WriteByte(0);

        // index[0]: raw block at offset 0
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes(0x9300));
        ms.Write(new byte[24]);
        // index[1]: negative (corrupt) length
        ms.Write(BitConverter.GetBytes(0x9300u));
        ms.Write(BitConverter.GetBytes(unchecked((int)0xFFFFFFFF)));
        ms.Write(new byte[24]);

        File.WriteAllBytes(path, ms.ToArray());

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.CorruptFile, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void ExtractToBinCueWithCorruptBlockReturnsDecompressionError()
    {
        var path = Path.Combine(_tempDir, $"corruptblock_{Guid.NewGuid():N}.pbp");

        using var ms = new MemoryStream();
        WriteStandardPbpHeader(ms);
        ms.Write(BuildMinimalSfo());

        while (ms.Position < 0x200)
            ms.WriteByte(0);

        ms.Write("PSISOIMG0000"u8.ToArray());

        while (ms.Position < 0x200 + 0x4000)
            ms.WriteByte(0);

        // Two valid raw blocks, then a block with a negative (corrupt) length.
        for (var i = 0; i < 2; i++)
        {
            ms.Write(BitConverter.GetBytes((uint)(i * 0x9300)));
            ms.Write(BitConverter.GetBytes(0x9300));
            ms.Write(new byte[24]);
        }

        ms.Write(BitConverter.GetBytes(2u * 0x9300));
        ms.Write(BitConverter.GetBytes(unchecked((int)0xFFFFFFFF)));
        ms.Write(new byte[24]);

        // Raw ISO data for the two valid blocks (starts at psarOffset + 0x100000).
        while (ms.Position < 0x200 + 0x100000 + 2 * 0x9300)
            ms.WriteByte(0);

        File.WriteAllBytes(path, ms.ToArray());

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        using (pbp)
        {
            Assert.Equal(3, pbp.Discs[0].BlockCount);

            var binPath = Path.Combine(_tempDir, $"corrupt_{Guid.NewGuid():N}.bin");
            var cuePath = Path.ChangeExtension(binPath, ".cue");
            var result = pbp.Discs[0].ExtractToBinCue(binPath, cuePath);
            Assert.Equal(PbpError.DecompressionError, result);
        }
    }

    private static void WriteStandardPbpHeader(
        Stream ms,
        int sfoOffset = 0x28,
        int dataPsarOffset = 0x200
    )
    {
        ms.Write(BitConverter.GetBytes(PbpHeader.MagicValue));
        ms.Write(BitConverter.GetBytes(1u)); // version
        ms.Write(BitConverter.GetBytes(sfoOffset));
        ms.Write(BitConverter.GetBytes(0x100)); // icon0
        ms.Write(BitConverter.GetBytes(0x100)); // icon1
        ms.Write(BitConverter.GetBytes(0x100)); // pic0
        ms.Write(BitConverter.GetBytes(0x100)); // pic1
        ms.Write(BitConverter.GetBytes(0x100)); // snd0
        ms.Write(BitConverter.GetBytes(0x100)); // dataPsp
        ms.Write(BitConverter.GetBytes(dataPsarOffset)); // dataPsar
    }

    [Fact]
    public void DefaultPbpHeaderIsNotValid()
    {
        var header = default(PbpHeader);
        Assert.False(header.IsValid);
    }

    [Fact]
    public void PbpHeaderMagicValueIsCorrect()
    {
        Assert.Equal(0x50425000u, PbpHeader.MagicValue);
    }

    [Fact]
    public void PbpHeaderSizeIs40()
    {
        Assert.Equal(0x28, PbpHeader.HeaderSize);
    }

    [Fact]
    public void PbpHeaderConstructorSetsAllProperties()
    {
        var header = new PbpHeader(1, 0x28, 0x100, 0x200, 0x300, 0x400, 0x500, 0x600, 0x700);

        Assert.Equal(1u, header.Version);
        Assert.Equal(0x28, header.SfoOffset);
        Assert.Equal(0x100, header.Icon0Offset);
        Assert.Equal(0x200, header.Icon1Offset);
        Assert.Equal(0x300, header.Pic0Offset);
        Assert.Equal(0x400, header.Pic1Offset);
        Assert.Equal(0x500, header.Snd0Offset);
        Assert.Equal(0x600, header.DataPspOffset);
        Assert.Equal(0x700, header.DataPsarOffset);
        Assert.True(header.IsValid);
    }

    [Fact]
    public void SfoDataDefaultValuesAreCorrect()
    {
        var sfo = new SfoData();
        Assert.Equal(0u, sfo.Magic);
        Assert.Equal(0u, sfo.Version);
        Assert.Equal(0u, sfo.KeyTableOffset);
        Assert.Equal(0u, sfo.DataTableOffset);
        Assert.NotNull(sfo.Entries);
        Assert.Empty(sfo.Entries);
    }

    [Fact]
    public void SfoDataGetStringReturnsNullForMissingKey()
    {
        var sfo = new SfoData();
        Assert.Null(sfo.GetString("NONEXISTENT"));
    }

    [Fact]
    public void SfoDataGetUInt32ReturnsNullForMissingKey()
    {
        var sfo = new SfoData();
        Assert.Null(sfo.GetUInt32("NONEXISTENT"));
    }

    [Fact]
    public void SfoDataKeysClassHasExpectedConstants()
    {
        Assert.Equal("BOOTABLE", SfoData.Keys.Bootable);
        Assert.Equal("CATEGORY", SfoData.Keys.Category);
        Assert.Equal("DISC_ID", SfoData.Keys.DiscId);
        Assert.Equal("TITLE", SfoData.Keys.Title);
    }

    [Fact]
    public void SfoEntryDefaultValuesAreCorrect()
    {
        var entry = new SfoEntry();
        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(0, entry.Format);
        Assert.Equal(0u, entry.Length);
        Assert.Equal(0u, entry.MaxLength);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void TocEntryDefaultValuesAreCorrect()
    {
        var entry = new TocEntry();
        Assert.Equal(0, (int)entry.TrackType);
        Assert.Equal(0, entry.TrackNo);
        Assert.Equal(0, entry.Minutes);
        Assert.Equal(0, entry.Seconds);
        Assert.Equal(0, entry.Frames);
    }

    [Fact]
    public void TrackTypeDataValue()
    {
        Assert.Equal(0x41, (int)TrackType.Data);
    }

    [Fact]
    public void TrackTypeAudioValue()
    {
        Assert.Equal(0x01, (int)TrackType.Audio);
    }

    [Fact]
    public void PbpErrorEnumHasExpectedValues()
    {
        Assert.Equal(0, (int)PbpError.None);
        Assert.Equal(1, (int)PbpError.InvalidHeader);
        Assert.Equal(2, (int)PbpError.FileNotFound);
        Assert.Equal(3, (int)PbpError.IoError);
        Assert.Equal(4, (int)PbpError.CorruptFile);
        Assert.Equal(5, (int)PbpError.InvalidPsarHeader);
        Assert.Equal(6, (int)PbpError.DiscOutOfRange);
        Assert.Equal(7, (int)PbpError.ResourceNotFound);
        Assert.Equal(8, (int)PbpError.DecompressionError);
    }

    [Fact]
    public void PbpDiscInfoIsoBlockSizeIsCorrect()
    {
        Assert.Equal(0x930, PbpDiscInfo.IsoBlockSize);
    }

    private string CreatePbpWithInvalidPsar()
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.pbp");

        const int sfoOffset = 0x28;
        const int dataPsarOffset = 0x200;

        using var ms = new MemoryStream();

        // PBP Header (40 bytes)
        ms.Write(BitConverter.GetBytes(PbpHeader.MagicValue));
        ms.Write(BitConverter.GetBytes(1u)); // version
        ms.Write(BitConverter.GetBytes(sfoOffset));
        ms.Write(BitConverter.GetBytes(0x100)); // icon0
        ms.Write(BitConverter.GetBytes(0x100)); // icon1
        ms.Write(BitConverter.GetBytes(0x100)); // pic0
        ms.Write(BitConverter.GetBytes(0x100)); // pic1
        ms.Write(BitConverter.GetBytes(0x100)); // snd0
        ms.Write(BitConverter.GetBytes(0x100)); // dataPsp
        ms.Write(BitConverter.GetBytes(dataPsarOffset)); // dataPsar

        // Minimal SFO at 0x28
        ms.Write(BuildMinimalSfo());

        // Pad to dataPsarOffset
        while (ms.Position < dataPsarOffset)
            ms.WriteByte(0);

        // Write invalid PSAR header (not PSISOIMG0000 or PSTITLEIMG000000)
        var invalidHeader = "INVALID_PSAR!"u8.ToArray();
        ms.Write(invalidHeader);
        ms.Write(new byte[4]); // pad to 16

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static byte[] BuildMinimalSfo()
    {
        using var ms = new MemoryStream();

        // SFO Header (16 bytes)
        ms.Write(BitConverter.GetBytes(0x46535000u)); // magic
        ms.Write(BitConverter.GetBytes(0x00000101u)); // version
        var keyTableOffsetPos = ms.Position;
        ms.Write(BitConverter.GetBytes(0u)); // placeholder
        var dataTableOffsetPos = ms.Position;
        ms.Write(BitConverter.GetBytes(0u)); // placeholder
        var entryCountPos = ms.Position;
        ms.Write(BitConverter.GetBytes(0u)); // placeholder

        var entries = new List<(string Key, ushort Format, byte[] Data)>
        {
            ("TITLE", 0x0204, "Test Game"u8.ToArray()),
        };

        var entryCount = (uint)entries.Count;

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

            var dirEntry = new byte[16];
            BitConverter.GetBytes(keyOffset).CopyTo(dirEntry, 0);
            BitConverter.GetBytes(format).CopyTo(dirEntry, 2);
            BitConverter.GetBytes((uint)data.Length).CopyTo(dirEntry, 4);
            BitConverter.GetBytes((uint)Math.Max(data.Length, 32)).CopyTo(dirEntry, 8);
            BitConverter.GetBytes(dataOffset).CopyTo(dirEntry, 12);
            dirEntries.Add(dirEntry);
        }

        foreach (var dirEntry in dirEntries)
            ms.Write(dirEntry);

        var keyTableOffset = 16 + entryCount * 16;
        var dataTableOffset = (uint)(keyTableOffset + keyTable.Length);

        ms.Write(keyTable.ToArray());
        ms.Write(dataTable.ToArray());

        var sfoBytes = ms.ToArray();
        BitConverter.GetBytes(keyTableOffset).CopyTo(sfoBytes, (int)keyTableOffsetPos);
        BitConverter.GetBytes(dataTableOffset).CopyTo(sfoBytes, (int)dataTableOffsetPos);
        BitConverter.GetBytes(entryCount).CopyTo(sfoBytes, (int)entryCountPos);

        return sfoBytes;
    }

    // --- Synthetic PBP tests using PbpTestFileBuilder ---

    [Fact]
    public void OpenSyntheticSingleDiscCompressedReturnsSuccess()
    {
        var path = Path.Combine(_tempDir, $"synth_compressed_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder()
            .WithTitle("Synthetic Game")
            .WithDiscId("SLUS00001")
            .WithBlockCount(2)
            .WithCompressedBlocks(true)
            .BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);
        pbp.Dispose();
    }

    [Fact]
    public void OpenSyntheticSingleDiscUncompressedReturnsSuccess()
    {
        var path = Path.Combine(_tempDir, $"synth_uncompressed_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).WithCompressedBlocks(false).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);
        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpSfoMetadataIsParsed()
    {
        var path = Path.Combine(_tempDir, $"synth_sfo_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder()
            .WithTitle("My Test Game")
            .WithDiscId("SCUS94163")
            .WithCategory("ME")
            .BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        Assert.Equal("My Test Game", pbp.Title);
        Assert.Equal("SCUS94163", pbp.DiscId);
        Assert.Equal("ME", pbp.Category);
        Assert.NotEmpty(pbp.SfoData.Entries);
        Assert.Equal("My Test Game", pbp.SfoData.GetString("TITLE"));
        Assert.Equal("SCUS94163", pbp.SfoData.GetString("DISC_ID"));
        Assert.Equal(1u, pbp.SfoData.GetUInt32("BOOTABLE"));

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpHeaderIsValid()
    {
        var path = Path.Combine(_tempDir, $"synth_header_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);
        Assert.True(pbp.Header.IsValid);
        Assert.Equal(0x50425000u, PbpHeader.MagicValue);
        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpIsSingleDisc()
    {
        var path = Path.Combine(_tempDir, $"synth_single_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);
        Assert.Single(pbp.Discs);
        Assert.False(pbp.IsMultiDisc);
        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpDiscHasTocEntries()
    {
        var path = Path.Combine(_tempDir, $"synth_toc_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        Assert.NotEmpty(disc.Toc);
        Assert.True(disc.Toc[0].TrackNo > 0);

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpDiscHasPositiveBlockCount()
    {
        var path = Path.Combine(_tempDir, $"synth_blocks_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(3).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        Assert.Equal(3, pbp.Discs[0].BlockCount);
        Assert.True(pbp.Discs[0].IsoSize > 0);

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpReadBlockReturnsData()
    {
        var path = Path.Combine(_tempDir, $"synth_readblock_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        var buffer = new byte[16 * PbpDiscInfo.IsoBlockSize];
        disc.ReadBlock(0, buffer, out var bytesRead);
        Assert.True(bytesRead > 0);

        // Verify the ISO size is encoded at bytes 104-107
        var sectorCount = BitConverter.ToUInt32(buffer, 104);
        Assert.True(sectorCount > 0);

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpReadBlockOutOfRangeThrows()
    {
        var path = Path.Combine(_tempDir, $"synth_oob_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var buffer = new byte[16 * PbpDiscInfo.IsoBlockSize];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pbp.Discs[0].ReadBlock(999, buffer, out _)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => pbp.Discs[0].ReadBlock(-1, buffer, out _));

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpExtractToProducesCorrectSize()
    {
        var path = Path.Combine(_tempDir, $"synth_extract_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        var expectedSize = disc.IsoSize;

        using var outputStream = new MemoryStream();
        disc.ExtractTo(outputStream);

        Assert.Equal(expectedSize, (uint)outputStream.Length);

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpExtractToReportsProgress()
    {
        var path = Path.Combine(_tempDir, $"synth_progress_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(3).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var progressValues = new List<uint>();
        using var outputStream = new MemoryStream();
        pbp.Discs[0].ExtractTo(outputStream, p => progressValues.Add(p));

        Assert.NotEmpty(progressValues);
        // Progress should be monotonically increasing
        for (var i = 1; i < progressValues.Count; i++)
            Assert.True(progressValues[i] >= progressValues[i - 1]);

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpExtractToRespectsCancellation()
    {
        var path = Path.Combine(_tempDir, $"synth_cancel_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(5).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        using var outputStream = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() =>
            pbp.Discs[0].ExtractTo(outputStream, null, cts.Token)
        );

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpExtractToBinCueReturnsSuccess()
    {
        var path = Path.Combine(_tempDir, $"synth_bincue_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var binPath = Path.Combine(_tempDir, $"synth_{Guid.NewGuid():N}.bin");
        var cuePath = Path.ChangeExtension(binPath, ".cue");

        error = pbp.Discs[0].ExtractToBinCue(binPath, cuePath);
        Assert.Equal(PbpError.None, error);
        Assert.True(File.Exists(binPath));
        Assert.True(File.Exists(cuePath));

        var binSize = new FileInfo(binPath).Length;
        Assert.True(binSize > 0);

        var cueContent = File.ReadAllText(cuePath);
        Assert.Contains("FILE", cueContent, StringComparison.Ordinal);
        Assert.Contains("TRACK", cueContent, StringComparison.Ordinal);
        Assert.Contains("INDEX", cueContent, StringComparison.Ordinal);

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpExtractToBinCueGeneratesValidCue()
    {
        var path = Path.Combine(_tempDir, $"synth_cue_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        var generatedCue = CueSheetWriter.GenerateCueSheet("test.bin", disc.Toc);

        Assert.Contains("FILE \"test.bin\" BINARY", generatedCue, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE2/2352", generatedCue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01", generatedCue, StringComparison.Ordinal);

        pbp.Dispose();
    }

    [Fact]
    public void OpenSyntheticPbpFromStreamReturnsSuccess()
    {
        var bytes = new PbpTestFileBuilder().Build();
        using var stream = new MemoryStream(bytes);

        var error = PbpFile.Open(stream, false, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);
        Assert.Equal("Test Game", pbp.Title);
        pbp.Dispose();
    }

    [Fact]
    public void OpenSyntheticPbpFromStreamWithOwnershipDisposesStream()
    {
        var bytes = new PbpTestFileBuilder().Build();
        var stream = new MemoryStream(bytes);

        var error = PbpFile.Open(stream, true, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        pbp.Dispose();

        // Stream should be disposed after PbpFile disposal
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public void OpenSyntheticPbpFromStreamWithoutOwnershipDoesNotDisposeStream()
    {
        var bytes = new PbpTestFileBuilder().Build();
        var stream = new MemoryStream(bytes);

        var error = PbpFile.Open(stream, false, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        pbp.Dispose();

        // Stream should still be usable (ownership was not transferred)
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        stream.Seek(0, SeekOrigin.Begin);
        Assert.Equal(0, stream.ReadByte());
        stream.Dispose();
    }

    [Fact]
    public void OpenNonSeekableStreamReturnsIoError()
    {
        var bytes = new PbpTestFileBuilder().Build();
        using var stream = new NonSeekableStream(bytes);

        var error = PbpFile.Open(stream, false, out var pbp);
        Assert.Equal(PbpError.IoError, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void OpenNonReadableStreamReturnsIoError()
    {
        var bytes = new PbpTestFileBuilder().Build();
        using var ms = new MemoryStream(bytes);
        using var stream = new WriteOnlyStream(ms);

        var error = PbpFile.Open(stream, false, out var pbp);
        Assert.Equal(PbpError.IoError, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void SyntheticMultiDiscPbpReturnsSuccess()
    {
        var path = Path.Combine(_tempDir, $"synth_multidisc_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().AsMultiDisc(0x200000, 0x400000).WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);
        Assert.True(pbp.IsMultiDisc);
        Assert.Equal(2, pbp.Discs.Count);
        pbp.Dispose();
    }

    [Fact]
    public void SyntheticMultiDiscPbpDiscIdsAreCorrect()
    {
        var path = Path.Combine(_tempDir, $"synth_multidisc_id_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder()
            .WithDiscId("SLUS00001")
            .AsMultiDisc(0x200000, 0x400000)
            .BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        foreach (var disc in pbp.Discs)
        {
            Assert.False(string.IsNullOrWhiteSpace(disc.DiscId));
            Assert.NotEmpty(disc.Toc);
        }

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticMultiDiscPbpExtractToProducesData()
    {
        var path = Path.Combine(_tempDir, $"synth_multidisc_extract_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().AsMultiDisc(0x200000, 0x400000).WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        foreach (var disc in pbp.Discs)
        {
            using var outputStream = new MemoryStream();
            disc.ExtractTo(outputStream);
            Assert.True(outputStream.Length > 0);
            Assert.Equal(disc.IsoSize, (uint)outputStream.Length);
        }

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpDisposeMultipleTimesDoesNotThrow()
    {
        var path = Path.Combine(_tempDir, $"synth_dispose_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        pbp.Dispose();
        var exception = Record.Exception(() => pbp.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void SyntheticPbpExtractedDataIsConsistent()
    {
        var path = Path.Combine(_tempDir, $"synth_consistent_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];

        // Extract twice and compare
        using var stream1 = new MemoryStream();
        disc.ExtractTo(stream1);

        // Reset and extract again (need to re-read blocks)
        using var stream2 = new MemoryStream();
        disc.ExtractTo(stream2);

        Assert.Equal(stream1.Length, stream2.Length);
        Assert.True(stream1.ToArray().SequenceEqual(stream2.ToArray()));

        pbp.Dispose();
    }

    [Fact]
    public void SyntheticPbpExtractedContentMatchesBuilderPattern()
    {
        // Round-trip check: extraction must reproduce the exact ISO data that was
        // packed into the PSAR, verifying offsets, decompression, and ordering.
        const int blockCount = 3;
        var path = Path.Combine(_tempDir, $"synth_roundtrip_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder()
            .WithBlockCount(blockCount)
            .WithCompressedBlocks(true)
            .BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        const int blockSize = 16 * PbpDiscInfo.IsoBlockSize;
        var expected = new byte[blockCount * blockSize];

        // Block 1 carries a recognizable pattern plus the sector count at bytes 104-107;
        // all other blocks use the (i + block*17) pattern (see PbpTestFileBuilder).
        for (var i = 0; i < blockSize; i++)
            expected[blockSize + i] = (byte)((i + 1) & 0xFF);
        BitConverter.GetBytes((uint)(blockCount * 16)).CopyTo(expected, blockSize + 104);

        for (var b = 0; b < blockCount; b++)
        {
            if (b == 1)
                continue;
            for (var i = 0; i < blockSize; i++)
                expected[b * blockSize + i] = (byte)((i + b * 17) & 0xFF);
        }

        using var outputStream = new MemoryStream();
        pbp.Discs[0].ExtractTo(outputStream);

        Assert.Equal(expected.Length, outputStream.Length);
        Assert.True(
            expected.AsSpan().SequenceEqual(outputStream.ToArray()),
            "Extracted ISO data does not match the data written into the PBP"
        );

        pbp.Dispose();
    }

    [Fact]
    public void OpenSyntheticSingleBlockPbpReturnsCorruptFile()
    {
        // The PSAR format stores the ISO size inside block index 1 (the second block),
        // so a PSAR with a single indexed block cannot expose a usable ISO size. This
        // mirrors the reference implementation, which also throws when reading the size
        // from such a file.
        var path = Path.Combine(_tempDir, $"synth_singleblock_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(1).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.CorruptFile, error);
        Assert.Null(pbp);
    }

    [Fact]
    public void SyntheticPbpLargeBlockCount()
    {
        var path = Path.Combine(_tempDir, $"synth_large_{Guid.NewGuid():N}.pbp");
        new PbpTestFileBuilder().WithBlockCount(10).BuildTo(path);

        var error = PbpFile.Open(path, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        Assert.Equal(10, disc.BlockCount);

        using var outputStream = new MemoryStream();
        disc.ExtractTo(outputStream);
        Assert.Equal(disc.IsoSize, (uint)outputStream.Length);

        pbp.Dispose();
    }

    /// <summary>
    /// A stream that is not seekable, used to test PbpFile.Open rejection.
    /// </summary>
    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override bool CanSeek => false;
    }

    /// <summary>
    /// A stream that is not readable, used to test PbpFile.Open rejection.
    /// </summary>
    private sealed class WriteOnlyStream : Stream
    {
        private readonly Stream _inner;

        public WriteOnlyStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}