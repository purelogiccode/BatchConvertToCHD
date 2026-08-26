using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class IsoSectorValidatorTests : IDisposable
{
    private readonly string _tempDir;

    public IsoSectorValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"IsoSectorValidatorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name, long size)
    {
        var path = Path.Combine(_tempDir, name);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);

        return path;
    }

    [Fact]
    public void SectorAlignedIsoReturnsNoWarning()
    {
        var path = CreateFile("game.iso", 2048 * 100);

        var warning = IsoSectorValidator.GetSectorSizeWarning(path);

        Assert.Null(warning);
    }

    [Fact]
    public void Cd2352SizedIsoReturnsNoWarning()
    {
        // A CD image saved with .iso extension uses 2352-byte sectors and must not be flagged.
        var path = CreateFile("cdgame.iso", 2352 * 100);

        var warning = IsoSectorValidator.GetSectorSizeWarning(path);

        Assert.Null(warning);
    }

    [Fact]
    public void MisalignedIsoReturnsWarning()
    {
        var path = CreateFile("game.iso", 2048 * 100 + 1);

        var warning = IsoSectorValidator.GetSectorSizeWarning(path);

        Assert.NotNull(warning);
        Assert.Contains(
            "not divisible by any standard sector size",
            warning,
            StringComparison.Ordinal
        );
        Assert.Contains("corrupt or truncated", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TextDescriptorsAreNeverValidated()
    {
        var cue = CreateFile("game.cue", 12345);

        var warning = IsoSectorValidator.GetSectorSizeWarning(cue);

        Assert.Null(warning);
    }

    [Fact]
    public void EmptyFileReturnsNoWarning()
    {
        var path = CreateFile("game.iso", 0);

        var warning = IsoSectorValidator.GetSectorSizeWarning(path);

        Assert.Null(warning);
    }

    [Fact]
    public void MissingFileReturnsNoWarning()
    {
        var warning = IsoSectorValidator.GetSectorSizeWarning(
            Path.Combine(_tempDir, "missing.iso")
        );

        Assert.Null(warning);
    }
}
