using BatchConvertToCHD.Services;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     Tests for the verification checksum report writer.
/// </summary>
public class ChdChecksumReportTests : IDisposable
{
    private readonly string _tempDir;

    public ChdChecksumReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChdChecksumReportTests_{Guid.NewGuid():N}");
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
    public void Write_CdReportHasWholeImageAndPerTrackHashes()
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

        var reportPath = ChdChecksumReport.Write(
            chdPath,
            "abcdef0123456789",
            null,
            CancellationToken.None
        );

        Assert.Equal(Path.ChangeExtension(chdPath, ChdChecksumReport.ReportExtension), reportPath);
        var report = File.ReadAllText(reportPath);
        Assert.Contains("SHA-1:      abcdef0123456789", report, StringComparison.Ordinal);
        Assert.Contains("Track 01", report, StringComparison.Ordinal);
        Assert.Contains("CRC-32:", report, StringComparison.Ordinal);
        Assert.Contains("XXH3-64:", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_DvdReportHasWholeImageHashesAndNoTracks()
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

        var reportPath = ChdChecksumReport.Write(chdPath, null, null, CancellationToken.None);

        var report = File.ReadAllText(reportPath);
        Assert.DoesNotContain("Track 01", report, StringComparison.Ordinal);
        Assert.DoesNotContain("(not available)", report, StringComparison.Ordinal);
        Assert.Contains("CRC-32:", report, StringComparison.Ordinal);
        Assert.Contains("XXH3-64:", report, StringComparison.Ordinal);
    }
}
