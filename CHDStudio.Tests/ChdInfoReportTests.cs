using CHDStudio.Services;
using CHDStudio.Utilities;

namespace CHDStudio.Tests;

/// <summary>
///     Tests for the Explorer's Image Info report builder.
/// </summary>
public class ChdInfoReportTests : IDisposable
{
    private readonly string _tempDir;

    public ChdInfoReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChdInfoReportTests_{Guid.NewGuid():N}");
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

    [Fact]
    public void Build_ReportsCdHeaderTracksMetadataAndHunks()
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

        var report = ChdInfoReport.Build(chdPath);

        Assert.Contains("Version:     V5", report, StringComparison.Ordinal);
        Assert.Contains("Type:        CD", report, StringComparison.Ordinal);
        Assert.Contains("Metadata:", report, StringComparison.Ordinal);
        Assert.Contains("Tracks:", report, StringComparison.Ordinal);
        Assert.Contains("Hunks:", report, StringComparison.Ordinal);
        Assert.Contains("SHA-1:", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReportsDvdWholeImageWithoutTracks()
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

        var report = ChdInfoReport.Build(chdPath);

        Assert.Contains("Type:        DVD", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Tracks:", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnreadableHeaderIsReportedNotThrown()
    {
        var path = PathFor("garbage.chd");
        File.WriteAllBytes(path, new byte[1024]);

        var report = ChdInfoReport.Build(path);

        Assert.Contains("could not be read", report, StringComparison.Ordinal);
    }
}
