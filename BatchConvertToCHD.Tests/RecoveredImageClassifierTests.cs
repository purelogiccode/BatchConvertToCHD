using Alcohol120Sharp;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     Covers the layout classification a recovered image (decoded ECM, decompressed ISZ, joined split
///     set or extracted archive) goes through before chdman sees it.
/// </summary>
public class RecoveredImageClassifierTests : IDisposable
{
    private const string MisalignedReason = "the image matches no sector layout, so it is probably damaged.";

    private readonly List<string> _log = [];
    private readonly string _tempDir;

    public RecoveredImageClassifierTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"RecoveredImageClassifierTests_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes an image made of the given bytes and returns its path.</summary>
    private string WriteImage(string name, byte[] bytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    /// <summary>Writes an image of <paramref name="sectors" /> identical units.</summary>
    private string WriteSectors(string name, byte[] sectorBytes, int sectors)
    {
        return WriteImage(name, Repeat(sectorBytes, sectors));
    }

    /// <summary>Builds a raw CD sector with the sync mark and the requested mode byte.</summary>
    private static byte[] RawSector(byte mode)
    {
        var sector = new byte[RawCdImageDetector.RawSectorSize];

        // 12-byte sync: 00 FF x10 00
        sector[0] = 0x00;
        for (var i = 1; i <= 10; i++) sector[i] = 0xFF;

        sector[11] = 0x00;
        sector[12] = 0x00;
        sector[13] = 0x02;
        sector[14] = 0x00;
        sector[15] = mode;

        return sector;
    }

    /// <summary>Appends a subchannel tail of <paramref name="tailBytes" /> to a raw sector.</summary>
    private static byte[] WithSubchannelTail(byte mode, int tailBytes)
    {
        var unit = new byte[RawCdImageDetector.RawSectorSize + tailBytes];
        RawSector(mode).CopyTo(unit, 0);
        for (var i = RawCdImageDetector.RawSectorSize; i < unit.Length; i++) unit[i] = 0xAA;

        return unit;
    }

    /// <summary>Repeats one sector unit into a whole image.</summary>
    private static byte[] Repeat(byte[] unit, int sectors)
    {
        var data = new byte[unit.Length * sectors];
        for (var sector = 0; sector < sectors; sector++) unit.CopyTo(data, sector * unit.Length);

        return data;
    }

    private async Task<RecoveredImageClassifier.Result> ClassifyAsync(
        string imagePath,
        string? workDir = null
    )
    {
        workDir ??= Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);

        return await RecoveredImageClassifier.ClassifyAsync(
            imagePath,
            workDir,
            "Decoded image",
            MisalignedReason,
            _log.Add,
            CancellationToken.None
        );
    }

    #region Raw CD and DVD layouts

    [Fact]
    public async Task RawCdSectorsGetACue()
    {
        var image = WriteSectors("raw.bin", RawSector(0x02), 4);

        var result = await ClassifyAsync(image);

        Assert.True(result.Success);
        Assert.Null(result.DvdImagePath);
        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("TRACK 01 MODE2/2352", cue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cooked2048ImageIsConvertedAsADvdImage()
    {
        var image = WriteSectors("dvd.iso", new byte[MdsDisc.CookedSectorSize], 64);

        var result = await ClassifyAsync(image);

        Assert.True(result.Success);
        Assert.Equal(image, result.DvdImagePath);
        Assert.Null(result.CuePath);
        Assert.Contains(_log, line => line.Contains("DVD image", StringComparison.Ordinal));
    }

    #endregion

    #region Mode 2 layouts

    [Fact]
    public async Task Mode2XaSectorsGetAMode2XaCue()
    {
        // 2336-byte Mode 2 sectors carry no sync header, so only the size can identify them. 13
        // sectors keeps the total clear of the other standard sector sizes.
        var image = WriteSectors("Game.mdf", new byte[RecoveredImageClassifier.Mode2XaSectorSize], 13);

        var result = await ClassifyAsync(image);

        Assert.True(result.Success);
        Assert.Null(result.DvdImagePath);
        Assert.NotNull(result.CuePath);

        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("TRACK 01 MODE2/2336", cue, StringComparison.Ordinal);
        Assert.Contains("FILE \"..\\Game.mdf\" BINARY", cue, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_tempDir, "work", "Game.mdf")));
    }

    [Fact]
    public async Task Mode2Form1SectorsGetAMode2Form1Cue()
    {
        var image = WriteSectors("Game.dat", new byte[RecoveredImageClassifier.Mode2Form1SectorSize], 13);

        var result = await ClassifyAsync(image);

        Assert.True(result.Success);
        Assert.Null(result.DvdImagePath);
        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("TRACK 01 MODE2/2324", cue, StringComparison.Ordinal);
    }

    #endregion

    #region Subchannel rips

    [Fact]
    public async Task SubchannelRipIsStrippedAndCued()
    {
        var image = Path.Combine(_tempDir, "Zero Divide.mdf");
        await File.WriteAllBytesAsync(image, Repeat(WithSubchannelTail(0x02, 96), 13));

        var result = await ClassifyAsync(image);

        Assert.True(result.Success);
        Assert.NotNull(result.CuePath);

        // The stripped copy keeps the 2352 data bytes of every sector and drops the 96-byte tail.
        var stripped = Path.Combine(_tempDir, "work", "Zero Divide.stripped.bin");
        Assert.True(File.Exists(stripped));
        Assert.Equal(13L * RawCdImageDetector.RawSectorSize, new FileInfo(stripped).Length);
        var strippedBytes = await File.ReadAllBytesAsync(stripped);
        Assert.DoesNotContain((byte)0xAA, strippedBytes);

        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("TRACK 01 MODE2/2352", cue, StringComparison.Ordinal);
        Assert.Contains(
            "FILE \"Zero Divide.stripped.bin\" BINARY",
            cue,
            StringComparison.Ordinal
        );
        Assert.Contains(_log, line => line.Contains("stripping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShortSubchannelRipIsStrippedAndCued()
    {
        var image = Path.Combine(_tempDir, "Game2.mdf");
        await File.WriteAllBytesAsync(image, Repeat(WithSubchannelTail(0x01, 16), 13));

        var result = await ClassifyAsync(image);

        Assert.True(result.Success);
        var stripped = Path.Combine(_tempDir, "work", "Game2.stripped.bin");
        Assert.Equal(13L * RawCdImageDetector.RawSectorSize, new FileInfo(stripped).Length);

        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("TRACK 01 MODE1/2352", cue, StringComparison.Ordinal);
    }

    #endregion

    #region Unusable images

    [Fact]
    public async Task SizeFittingNoLayoutIsSkippedWithTheGivenReason()
    {
        var image = WriteImage("damaged.mdf", new byte[5000]);

        var result = await ClassifyAsync(image);

        Assert.False(result.Success);
        Assert.Equal(MisalignedReason, result.SkipReason);
    }

    [Fact]
    public async Task EmptyAndMissingFilesAreSkipped()
    {
        var empty = WriteImage("empty.mdf", []);
        var missing = Path.Combine(_tempDir, "missing.mdf");

        Assert.False((await ClassifyAsync(empty)).Success);
        Assert.False((await ClassifyAsync(missing)).Success);
    }

    #endregion
}
