using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class RawCdImageDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public RawCdImageDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RawCdImageDetectorTests_{Guid.NewGuid():N}");
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

    /// <summary>Writes an image of whole 2352-byte sectors whose first sector carries a real sync mark.</summary>
    private string CreateRawCdImage(string name, byte mode, int sectors = 4)
    {
        var path = Path.Combine(_tempDir, name);
        var data = new byte[RawCdImageDetector.RawSectorSize * sectors];

        // 12-byte sync: 00 FF x10 00
        data[0] = 0x00;
        for (var i = 1; i <= 10; i++) data[i] = 0xFF;

        data[11] = 0x00;

        // 3-byte MSF address then the mode byte at offset 15.
        data[12] = 0x00;
        data[13] = 0x02;
        data[14] = 0x00;
        data[15] = mode;

        File.WriteAllBytes(path, data);

        return path;
    }

    private string CreateFile(string name, long size)
    {
        var path = Path.Combine(_tempDir, name);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);

        return path;
    }

    [Theory]
    [InlineData(".iso")]
    [InlineData(".img")]
    [InlineData(".bin")]
    [InlineData(".ISO")]
    public void CandidateExtensionsAreTheOnesRawDumpsAreMislabelledWith(string ext)
    {
        Assert.True(RawCdImageDetector.IsCandidateExtension(ext));
    }

    [Theory]
    [InlineData(".cue")]
    [InlineData(".chd")]
    [InlineData(".zip")]
    [InlineData(".gdi")]
    public void NonImageExtensionsAreNotCandidates(string ext)
    {
        Assert.False(RawCdImageDetector.IsCandidateExtension(ext));
    }

    [Fact]
    public void Mode2RawCdNamedIsoIsDetected()
    {
        // The real failure: a PlayStation raw dump distributed as .iso went to createdvd and died on
        // "Data size ... is not divisible by sector size 2048".
        var path = CreateRawCdImage("Final Fantasy Origins.iso", 2);

        Assert.Equal(BinCueGenerator.Mode2, RawCdImageDetector.DetectTrackMode(path));
    }

    [Fact]
    public void Mode1RawCdIsDetected()
    {
        var path = CreateRawCdImage("data.img", 1);

        Assert.Equal(BinCueGenerator.Mode1, RawCdImageDetector.DetectTrackMode(path));
    }

    [Fact]
    public void CookedDvdImageIsNotDetectedAsRawCd()
    {
        // 2048-multiple with no sync mark: a genuine DVD image that must keep going to createdvd.
        var path = CreateFile("ps2game.iso", 2048 * 64);

        Assert.Null(RawCdImageDetector.DetectTrackMode(path));
    }

    [Fact]
    public void SyncMarkWithoutSectorAlignmentIsRejected()
    {
        // A truncated dump: the header looks right but the file is not whole sectors, so it is not a
        // usable raw CD and must not be dressed up with a cue.
        var path = CreateRawCdImage("truncated.img", 2);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 17);
        }

        Assert.Null(RawCdImageDetector.DetectTrackMode(path));
    }

    [Fact]
    public void UnknownModeByteIsRejected()
    {
        var path = CreateRawCdImage("weird.img", 7);

        Assert.Null(RawCdImageDetector.DetectTrackMode(path));
    }

    [Fact]
    public void EmptyAndMissingFilesAreRejected()
    {
        Assert.Null(RawCdImageDetector.DetectTrackMode(CreateFile("empty.iso", 0)));
        Assert.Null(RawCdImageDetector.DetectTrackMode(Path.Combine(_tempDir, "nope.iso")));
    }

    [Fact]
    public async Task StagedCueReferencesTheImageRelativelyWithoutCopyingIt()
    {
        var image = CreateRawCdImage("Herc's Adventure.iso", 2);
        var workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);

        var cuePath = await RawCdImageDetector.TryWriteCueAsync(
            image,
            BinCueGenerator.Mode2,
            workDir,
            CancellationToken.None
        );

        Assert.NotNull(cuePath);
        Assert.Equal(workDir, Path.GetDirectoryName(cuePath));

        var content = await File.ReadAllTextAsync(cuePath);
        Assert.Contains("TRACK 01 MODE2/2352", content, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:00", content, StringComparison.Ordinal);

        // The FILE line must be relative, because chdman resolves it against the cue's own folder.
        Assert.Contains(
            "FILE \"..\\Herc's Adventure.iso\" BINARY",
            content,
            StringComparison.Ordinal
        );

        // The image is referenced, never duplicated.
        Assert.False(File.Exists(Path.Combine(workDir, "Herc's Adventure.iso")));

        // And the cue is written without a BOM, which chdman's parser cannot skip.
        var bytes = await File.ReadAllBytesAsync(cuePath);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public async Task StagedCueIsNamedAfterTheImage()
    {
        var image = CreateRawCdImage("WACKY_RACES.iso", 2);
        var workDir = Path.Combine(_tempDir, "work2");
        Directory.CreateDirectory(workDir);

        var cuePath = await RawCdImageDetector.TryWriteCueAsync(
            image,
            BinCueGenerator.Mode2,
            workDir,
            CancellationToken.None
        );

        Assert.Equal("WACKY_RACES.cue", Path.GetFileName(cuePath));
    }
}