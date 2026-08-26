using PBPSharp;
using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

[Trait("Category", "Integration")]
public class PbpFileIntegrationTests : IDisposable
{
    private const string SamplesDir = @"D:\Emulators\Programas e utilitarios\PsxPackager";
    private readonly string _tempDir;

    public PbpFileIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PbpIntegrationTests_{Guid.NewGuid():N}");
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

    private static bool SamplesExist()
    {
        return Directory.Exists(SamplesDir);
    }

    private static IEnumerable<string> GetPbpFiles()
    {
        if (!SamplesExist())
            yield break;

        foreach (var path in Directory.GetFiles(SamplesDir, "*.pbp"))
            yield return path;
    }

    private static IEnumerable<(
        string PbpPath,
        string BinPath,
        string CuePath
    )> GetPbpWithBinCuePairs()
    {
        if (!SamplesExist())
            yield break;

        var pbpFiles = Directory.GetFiles(SamplesDir, "*.pbp");
        foreach (var pbpPath in pbpFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(pbpPath);
            var binPath = Path.Combine(SamplesDir, baseName + ".bin");
            var cuePath = Path.Combine(SamplesDir, baseName + ".cue");

            if (File.Exists(binPath) && File.Exists(cuePath))
                yield return (pbpPath, binPath, cuePath);
        }
    }

    [Fact]
    public void OpenPbpFileReturnsSuccess()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            pbp.Dispose();
        }
    }

    [Fact]
    public void PbpHeaderIsValid()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            Assert.True(pbp.Header.IsValid);
            Assert.Equal(0x50425000u, PbpHeader.MagicValue);
            pbp.Dispose();
        }
    }

    [Fact]
    public void PbpSfoMetadataIsParsed()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            Assert.NotNull(pbp.SfoData);
            Assert.NotEmpty(pbp.SfoData.Entries);
            pbp.Dispose();
        }
    }

    [Fact]
    public void PbpHasTitle()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            Assert.False(
                string.IsNullOrWhiteSpace(pbp.Title),
                $"PBP missing title: {Path.GetFileName(pbpPath)}"
            );
            pbp.Dispose();
        }
    }

    [Fact]
    public void PbpHasDiscId()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            Assert.False(
                string.IsNullOrWhiteSpace(pbp.DiscId),
                $"PBP missing disc ID: {Path.GetFileName(pbpPath)}"
            );
            pbp.Dispose();
        }
    }

    [Fact]
    public void PbpCategoryIsMe()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            Assert.Equal("ME", pbp.Category);
            pbp.Dispose();
        }
    }

    [Fact]
    public void PbpIsSingleDisc()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);
            Assert.Single(pbp.Discs);
            Assert.False(pbp.IsMultiDisc);
            pbp.Dispose();
        }
    }

    [Fact]
    public void DiscHasTocEntries()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);

            var disc = pbp.Discs[0];
            Assert.NotEmpty(disc.Toc);
            Assert.True(disc.Toc[0].TrackNo > 0);
            pbp.Dispose();
        }
    }

    [Fact]
    public void DiscIsoSizeMatchesOriginalBin()
    {
        var pairs = GetPbpWithBinCuePairs().ToArray();
        Assert.NotEmpty(pairs);

        foreach (var (pbpPath, binPath, _) in pairs)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);

            var disc = pbp.Discs[0];
            var binSize = new FileInfo(binPath).Length;
            Assert.Equal(disc.IsoSize, binSize);

            pbp.Dispose();
        }
    }

    [Fact]
    public void DiscBlockCountIsPositive()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);

            var disc = pbp.Discs[0];
            Assert.True(disc.BlockCount > 0, $"PBP has no blocks: {Path.GetFileName(pbpPath)}");
            pbp.Dispose();
        }
    }

    [Fact]
    public void ExtractToProducesCorrectIso()
    {
        var pairs = GetPbpWithBinCuePairs().ToArray();
        Assert.NotEmpty(pairs);

        var (pbpPath, binPath, _) = pairs[0];

        var error = PbpFile.Open(pbpPath, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        var outputPath = Path.Combine(_tempDir, "extracted.iso");

        using (var outputStream = File.Create(outputPath))
        {
            disc.ExtractTo(outputStream);
        }

        var originalSize = new FileInfo(binPath).Length;
        var extractedSize = new FileInfo(outputPath).Length;
        Assert.Equal(originalSize, extractedSize);

        using var originalStream = File.OpenRead(binPath);
        using var extractedStream = File.OpenRead(outputPath);

        var buffer1 = new byte[81920];
        var buffer2 = new byte[81920];
        long offset = 0;
        int bytesRead1;
        while ((bytesRead1 = originalStream.Read(buffer1, 0, buffer1.Length)) > 0)
        {
            var bytesRead2 = extractedStream.Read(buffer2, 0, bytesRead1);
            Assert.Equal(bytesRead1, bytesRead2);

            Assert.True(
                buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead2)),
                $"Extracted ISO differs at offset {offset}"
            );
            offset += bytesRead1;
        }

        pbp.Dispose();
    }

    [Fact]
    public void ExtractToBinCueProducesValidFiles()
    {
        var pairs = GetPbpWithBinCuePairs().ToArray();
        Assert.NotEmpty(pairs);

        var (pbpPath, binPath, _) = pairs[0];

        var error = PbpFile.Open(pbpPath, out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        var disc = pbp.Discs[0];
        var outBinPath = Path.Combine(_tempDir, "extracted.bin");
        var outCuePath = Path.Combine(_tempDir, "extracted.cue");

        error = disc.ExtractToBinCue(outBinPath, outCuePath);
        Assert.Equal(PbpError.None, error);
        Assert.True(File.Exists(outBinPath));
        Assert.True(File.Exists(outCuePath));

        var originalBinSize = new FileInfo(binPath).Length;
        var extractedBinSize = new FileInfo(outBinPath).Length;
        Assert.Equal(originalBinSize, extractedBinSize);

        var cueContent = File.ReadAllText(outCuePath);
        Assert.Contains("FILE", cueContent, StringComparison.Ordinal);
        Assert.Contains("TRACK", cueContent, StringComparison.Ordinal);
        Assert.Contains("INDEX", cueContent, StringComparison.Ordinal);

        pbp.Dispose();
    }

    [Fact]
    public void GeneratedCueSheetMatchesOriginal()
    {
        var pairs = GetPbpWithBinCuePairs().ToArray();
        Assert.NotEmpty(pairs);

        foreach (var (pbpPath, _, cuePath) in pairs)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);

            var disc = pbp.Discs[0];
            var generatedCue = CueSheetWriter.GenerateCueSheet(
                Path.GetFileNameWithoutExtension(pbpPath) + ".bin",
                disc.Toc
            );

            var originalCue = File.ReadAllText(cuePath);

            Assert.Equal(NormalizeCue(originalCue), NormalizeCue(generatedCue));

            pbp.Dispose();
        }
    }

    private static string NormalizeCue(string cue)
    {
        return string.Join(
            "\n",
            cue.Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
        );
    }

    [Fact]
    public void ReadBlockProducesValidData()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        foreach (var pbpPath in pbpFiles)
        {
            var error = PbpFile.Open(pbpPath, out var pbp);
            Assert.Equal(PbpError.None, error);
            Assert.NotNull(pbp);

            var disc = pbp.Discs[0];
            var buffer = new byte[16 * PbpDiscInfo.IsoBlockSize];
            disc.ReadBlock(0, buffer, out var bytesRead);
            Assert.True(
                bytesRead > 0,
                $"ReadBlock returned 0 bytes for {Path.GetFileName(pbpPath)}"
            );

            pbp.Dispose();
        }
    }

    [Fact]
    public void DisposeMultipleTimesDoesNotThrow()
    {
        var pbpFiles = GetPbpFiles().ToArray();
        Assert.NotEmpty(pbpFiles);

        var error = PbpFile.Open(pbpFiles[0], out var pbp);
        Assert.Equal(PbpError.None, error);
        Assert.NotNull(pbp);

        pbp.Dispose();
        var exception = Record.Exception(() => pbp.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void OpenNonExistentFileReturnsFileNotFound()
    {
        var error = PbpFile.Open(@"D:\nonexistent_path_12345.pbp", out var pbp);
        Assert.Equal(PbpError.FileNotFound, error);
        Assert.Null(pbp);
    }
}
