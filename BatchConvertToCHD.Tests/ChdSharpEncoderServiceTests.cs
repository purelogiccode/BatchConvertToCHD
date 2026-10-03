using BatchConvertToCHD.Services;
using CHDSharp;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     Tests for the in-process CHDSharp encoder used on every platform (and as the Windows
///     fallback behind chdman).
/// </summary>
public class ChdSharpEncoderServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ChdSharpEncoderServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChdSharpEncoderServiceTests_{Guid.NewGuid():N}");
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

    private string PathFor(string name)
    {
        return Path.Combine(_tempDir, name);
    }

    private static ChdHeaderInfo ReadHeader(string chdPath)
    {
        var error = Chd.ReadHeader(chdPath, out var header);
        Assert.Equal(ChdError.Chderrnone, error);
        return header!;
    }

    private static void AssertVerifies(string chdPath)
    {
        using var stream = File.OpenRead(chdPath);
        var result = Chd.CheckFile(stream, Path.GetFileName(chdPath), true);
        Assert.True(result.IsSuccess, $"CHD verification failed: {result.Error.GetMessage()}");
    }

    [Fact]
    public void CreateCd_EncodesVerifiableCdChd()
    {
        File.WriteAllBytes(PathFor("game.bin"), new byte[2352 * 5]);
        var cuePath = PathFor("game.cue");
        File.WriteAllText(
            cuePath,
            "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );
        var chdPath = PathFor("game.chd");

        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(2448u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateDvd_EncodesVerifiableDvdChd()
    {
        File.WriteAllBytes(PathFor("disc.iso"), new byte[2048 * 20]);
        var chdPath = PathFor("disc.chd");

        ChdSharpEncoderService.Encode(
            "createdvd",
            PathFor("disc.iso"),
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(2048u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateHd_EncodesVerifiableHardDiskChd()
    {
        File.WriteAllBytes(PathFor("disk.img"), new byte[512 * 2048]);
        var chdPath = PathFor("disk.chd");

        ChdSharpEncoderService.Encode(
            "createhd",
            PathFor("disk.img"),
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(512u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateRaw_EncodesVerifiableRawChd()
    {
        File.WriteAllBytes(PathFor("track.raw"), new byte[2352 * 10]);
        var chdPath = PathFor("track.chd");

        ChdSharpEncoderService.Encode(
            "createraw",
            PathFor("track.raw"),
            chdPath,
            true,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(2352u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateDvd_ReportsHunkProgress()
    {
        File.WriteAllBytes(PathFor("disc.iso"), new byte[2048 * 20]);
        var chdPath = PathFor("disc.chd");
        var reported = new List<HunkProgress>();

        ChdSharpEncoderService.Encode(
            "createdvd",
            PathFor("disc.iso"),
            chdPath,
            false,
            2,
            CancellationToken.None,
            reported.Add
        );

        Assert.Equal(10, reported.Count);
        Assert.All(reported, p => Assert.Equal(10u, p.HunkCount));
        Assert.Equal(0u, reported[0].HunkIndex);
        Assert.Equal(9u, reported[^1].HunkIndex);
    }

    [Fact]
    public void CreateLd_EncodesVerifiableLaserdiscChd()
    {
        var aviPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "laserdisc-small.avi");
        Assert.True(File.Exists(aviPath), $"AVI fixture missing: {aviPath}");
        var chdPath = PathFor("laserdisc.chd");
        var reported = new List<HunkProgress>();

        ChdSharpEncoderService.Encode(
            "createld",
            aviPath,
            chdPath,
            false,
            2,
            CancellationToken.None,
            reported.Add
        );

        AssertVerifies(chdPath);
        Assert.Equal(4u, ReadHeader(chdPath).TotalHunks);
        Assert.Equal(4, reported.Count);
    }

    [Fact]
    public void CreateLd_NonAviInput_Throws()
    {
        var input = PathFor("not-an-avi.bin");
        File.WriteAllBytes(input, new byte[4096]);

        Assert.Throws<InvalidDataException>(() =>
            ChdSharpEncoderService.Encode(
                "createld",
                input,
                PathFor("not-an-avi.chd"),
                false,
                1,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task DetectExtractCommand_CdChdIsExtractCd()
    {
        File.WriteAllBytes(PathFor("game.bin"), new byte[2352 * 5]);
        var cuePath = PathFor("game.cue");
        File.WriteAllText(
            cuePath,
            "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );
        var chdPath = PathFor("game.chd");
        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        var command = await MainWindow.DetectChdExtractCommandAsync(
            chdPath,
            CancellationToken.None
        );

        Assert.Equal("extractcd", command);
    }

    [Fact]
    public async Task DetectExtractCommand_DvdChdIsExtractDvd()
    {
        File.WriteAllBytes(PathFor("disc.iso"), new byte[2048 * 20]);
        var chdPath = PathFor("disc.chd");
        ChdSharpEncoderService.Encode(
            "createdvd",
            PathFor("disc.iso"),
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        var command = await MainWindow.DetectChdExtractCommandAsync(
            chdPath,
            CancellationToken.None
        );

        Assert.Equal("extractdvd", command);
    }

    [Fact]
    public async Task DetectExtractCommand_LaserdiscChdIsExtractLd()
    {
        var aviPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "laserdisc-small.avi");
        Assert.True(File.Exists(aviPath), $"AVI fixture missing: {aviPath}");
        var chdPath = PathFor("laserdisc.chd");
        ChdSharpEncoderService.Encode(
            "createld",
            aviPath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        var command = await MainWindow.DetectChdExtractCommandAsync(
            chdPath,
            CancellationToken.None
        );

        Assert.Equal("extractld", command);
    }

    [Theory]
    [InlineData("createdvd", 2048, false)]
    [InlineData("createhd", 512, false)]
    [InlineData("createraw", 2352, true)]
    public void PartialTrailingUnit_IsRejected(string command, int unitBytes, bool rawUnits)
    {
        // chdman refuses a size that is not a whole number of units; the built-in encoder must
        // too, otherwise the tail is silently dropped and the source could be deleted as success.
        var input = PathFor("partial.img");
        File.WriteAllBytes(input, new byte[unitBytes * 3 + 10]);

        Assert.Throws<InvalidDataException>(() =>
            ChdSharpEncoderService.Encode(
                command,
                input,
                PathFor("partial.chd"),
                rawUnits,
                1,
                CancellationToken.None
            )
        );
        Assert.False(File.Exists(PathFor("partial.chd")));
    }

    [Fact]
    public void UnsupportedCommand_Throws()
    {
        var input = PathFor("in.bin");
        File.WriteAllBytes(input, new byte[16]);

        Assert.Throws<ArgumentException>(() =>
            ChdSharpEncoderService.Encode(
                "createx",
                input,
                PathFor("out.chd"),
                false,
                1,
                CancellationToken.None
            )
        );
    }
}