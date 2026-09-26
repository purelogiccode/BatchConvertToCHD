using PBPSharp;
using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     The <see cref="PbpDiagnostics" /> side channel attaches the failing PSAR block's identity to
///     decompression failures, which the bare <see cref="PbpError" /> code cannot carry.
/// </summary>
public class PbpDiagnosticsTests : IDisposable
{
    private readonly string _tempDir;

    public PbpDiagnosticsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PbpDiagnosticsTests_{Guid.NewGuid():N}");
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
            // best effort
        }
    }

    [Fact]
    public void TakeDetailReturnsTheRecordedDetailAndClearsIt()
    {
        PbpDiagnostics.SetDetail("block 4 failed");

        Assert.Equal("block 4 failed", PbpDiagnostics.TakeDetail());
        Assert.Null(PbpDiagnostics.TakeDetail());
    }

    [Fact]
    public void ExtractionOfCorruptBlockRecordsBlockDiagnostics()
    {
        var pbpPath = Path.Combine(_tempDir, "corrupt.pbp");
        new PbpTestFileBuilder()
            .WithBlockCount(3)
            .WithCorruptBlock(2)
            .BuildTo(pbpPath);

        var openError = PbpFile.Open(pbpPath, out var pbpFile);
        Assert.Equal(PbpError.None, openError);

        using (pbpFile)
        {
            var error = pbpFile!.Discs[0]
                .ExtractToBinCue(
                    Path.Combine(_tempDir, "out.bin"),
                    null,
                    null,
                    CancellationToken.None
                );

            Assert.Equal(PbpError.DecompressionError, error);
        }

        var detail = PbpDiagnostics.TakeDetail();
        Assert.NotNull(detail);
        Assert.Contains("block 2 of 3", detail, StringComparison.Ordinal);
        Assert.Contains("length=", detail, StringComparison.Ordinal);
        Assert.Contains("first bytes", detail, StringComparison.Ordinal);
    }
}
