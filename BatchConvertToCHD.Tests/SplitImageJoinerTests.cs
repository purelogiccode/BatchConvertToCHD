using Alcohol120Sharp;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class SplitImageJoinerTests : IDisposable
{
    private readonly string _tempDir;

    public SplitImageJoinerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SplitImageJoinerTests_{Guid.NewGuid():N}");
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

    private string WritePart(string name, byte fill, int length)
    {
        var path = Path.Combine(_tempDir, name);
        var data = new byte[length];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);

        return path;
    }

    [Fact]
    public void NumberedSetIsFoundInOrder()
    {
        // The Final Fantasy VIII layout: plain byte-splits wearing an archive extension.
        var first = WritePart("FF8 CD1.rar.001", 1, 10);
        WritePart("FF8 CD1.rar.002", 2, 10);
        WritePart("FF8 CD1.rar.003", 3, 5);

        var parts = SplitImageJoiner.TryGetVolumeSet(first);

        Assert.NotNull(parts);
        Assert.Equal(3, parts.Count);
        Assert.Equal(first, parts[0]);
        Assert.EndsWith(".002", parts[1], StringComparison.Ordinal);
        Assert.EndsWith(".003", parts[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AlcoholSetIsFoundInOrder()
    {
        // The Xenosaga layout: Alcohol split the .mdf into .I00 / .I01.
        var first = WritePart("Xenosaga II Disc 2.I00", 1, 8);
        WritePart("Xenosaga II Disc 2.I01", 2, 8);

        var parts = SplitImageJoiner.TryGetVolumeSet(first);

        Assert.NotNull(parts);
        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public void EnumerationStopsAtTheFirstGap()
    {
        var first = WritePart("game.001", 1, 4);
        WritePart("game.002", 2, 4);
        WritePart("game.004", 4, 4);

        var parts = SplitImageJoiner.TryGetVolumeSet(first);

        Assert.NotNull(parts);
        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public void LoneFirstVolumeIsNotASet()
    {
        // A single file that merely ends in .001 must be left alone.
        var first = WritePart("solo.001", 1, 4);

        Assert.Null(SplitImageJoiner.TryGetVolumeSet(first));
    }

    [Theory]
    [InlineData("game.iso")]
    [InlineData("game.002")]
    [InlineData("game.i01")]
    [InlineData("game.rar")]
    [InlineData("game")]
    public void NonFirstVolumeNamesAreRejected(string name)
    {
        var path = WritePart(name, 1, 4);

        Assert.Null(SplitImageJoiner.TryGetVolumeSet(path));
    }

    [Fact]
    public async Task JoinConcatenatesInOrder()
    {
        var first = WritePart("j.001", 0xAA, 3);
        WritePart("j.002", 0xBB, 2);
        WritePart("j.003", 0xCC, 1);
        var parts = SplitImageJoiner.TryGetVolumeSet(first);
        var destination = Path.Combine(_tempDir, "joined.bin");

        var written = await SplitImageJoiner.JoinAsync(parts!, destination, CancellationToken.None);

        Assert.Equal(6, written);
        var bytes = await File.ReadAllBytesAsync(destination);
        Assert.Equal([0xAA, 0xAA, 0xAA, 0xBB, 0xBB, 0xCC], bytes);
    }

    [Fact]
    public void TotalBytesSumsEveryPart()
    {
        var first = WritePart("t.001", 1, 10);
        WritePart("t.002", 2, 7);
        var parts = SplitImageJoiner.TryGetVolumeSet(first);

        Assert.Equal(17, SplitImageJoiner.GetTotalBytes(parts!));
        Assert.Equal(0, SplitImageJoiner.GetTotalBytes([Path.Combine(_tempDir, "missing.bin")]));
    }

    [Fact]
    public async Task JoinedRawCdSetIsRecognisedAsAConvertibleImage()
    {
        // End to end for the split case: the parts alone are unreadable, the join is a valid raw CD.
        const int sectorSize = 2352;
        var firstData = new byte[sectorSize];
        for (var i = 1; i <= 10; i++) firstData[i] = 0xFF;

        firstData[15] = 2;

        var first = Path.Combine(_tempDir, "split.001");
        File.WriteAllBytes(first, firstData);
        File.WriteAllBytes(Path.Combine(_tempDir, "split.002"), new byte[sectorSize]);

        var parts = SplitImageJoiner.TryGetVolumeSet(first);
        var joined = Path.Combine(_tempDir, "joined.img");
        var written = await SplitImageJoiner.JoinAsync(parts!, joined, CancellationToken.None);

        Assert.Equal(sectorSize * 2, written);
        Assert.Equal(BinCueGenerator.Mode2, RawCdImageDetector.DetectTrackMode(joined));
    }
}