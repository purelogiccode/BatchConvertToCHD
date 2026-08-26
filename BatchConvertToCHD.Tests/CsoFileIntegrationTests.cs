using CSOSharp;
using CSOSharp.Models;

namespace BatchConvertToCHD.Tests;

[Trait("Category", "Integration")]
public class CsoFileIntegrationTests : IDisposable
{
    private const string SamplesDir = @"D:\Emulators\Programas e utilitarios\maxcso";
    private readonly string _tempDir;

    public CsoFileIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CsoIntegrationTests_{Guid.NewGuid():N}");
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

    private static IEnumerable<string[]> GetCsoWithIsoPairs()
    {
        if (!SamplesExist())
            yield break;

        var csoFiles = Directory.GetFiles(SamplesDir, "*.cso");
        foreach (var csoPath in csoFiles)
        {
            var isoPath = Path.ChangeExtension(csoPath, ".iso");
            if (File.Exists(isoPath))
                yield return [csoPath, isoPath];
        }
    }

    [Fact]
    public void OpenCsoFileReturnsValidHeader()
    {
        if (!SamplesExist())
            return;

        var csoFiles = Directory.GetFiles(SamplesDir, "*.cso");
        Assert.NotEmpty(csoFiles);

        foreach (var csoPath in csoFiles)
        {
            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);
            Assert.True(cso.Header.IsValid);
            Assert.True(cso.Header.BlockSize > 0);
            Assert.True(cso.Header.UncompressedSize > 0);
            Assert.True(cso.Header.TotalBlocks > 0);
            cso.Dispose();
        }
    }

    [Fact]
    public void CsoVersionIsV1Deflate()
    {
        if (!SamplesExist())
            return;

        var csoFiles = Directory.GetFiles(SamplesDir, "*.cso");
        Assert.NotEmpty(csoFiles);

        foreach (var csoPath in csoFiles)
        {
            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);
            Assert.True(
                cso.IsDeflate,
                $"Expected CSO v1 (deflate) for {Path.GetFileName(csoPath)}"
            );
            Assert.False(cso.IsLz4);
            cso.Dispose();
        }
    }

    [Fact]
    public void ReadFirstBlockSucceeds()
    {
        if (!SamplesExist())
            return;

        var csoFiles = Directory.GetFiles(SamplesDir, "*.cso");
        Assert.NotEmpty(csoFiles);

        foreach (var csoPath in csoFiles)
        {
            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);

            var buffer = new byte[cso.Header.BlockSize];
            error = cso.ReadBlock(0, buffer, out var bytesRead);
            Assert.Equal(CsoError.None, error);
            Assert.Equal((int)cso.Header.BlockSize, bytesRead);

            cso.Dispose();
        }
    }

    [Fact]
    public void ReadLastBlockSucceeds()
    {
        if (!SamplesExist())
            return;

        var csoFiles = Directory.GetFiles(SamplesDir, "*.cso");
        Assert.NotEmpty(csoFiles);

        foreach (var csoPath in csoFiles)
        {
            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);

            var lastBlock = cso.Header.TotalBlocks - 1;
            var buffer = new byte[cso.Header.BlockSize];
            error = cso.ReadBlock(lastBlock, buffer, out var bytesRead);
            Assert.Equal(CsoError.None, error);
            Assert.Equal((int)cso.Header.BlockSize, bytesRead);

            cso.Dispose();
        }
    }

    [Fact]
    public void DecpressedBlocksMatchIsoContent()
    {
        if (!SamplesExist())
            return;

        var pairs = GetCsoWithIsoPairs().ToArray();
        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var csoPath = pair[0];
            var isoPath = pair[1];

            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);

            using var isoStream = File.OpenRead(isoPath);
            var blockSize = (int)cso.Header.BlockSize;
            var csoBuffer = new byte[blockSize];
            var isoBuffer = new byte[blockSize];

            var blocksToCheck = Math.Min(cso.Header.TotalBlocks, 100u);
            for (uint i = 0; i < blocksToCheck; i++)
            {
                error = cso.ReadBlock(i, csoBuffer, out var bytesRead);
                Assert.Equal(CsoError.None, error);
                Assert.Equal(blockSize, bytesRead);

                var isoRead = isoStream.Read(isoBuffer, 0, blockSize);
                Assert.Equal(blockSize, isoRead);

                Assert.True(
                    csoBuffer.AsSpan().SequenceEqual(isoBuffer.AsSpan()),
                    $"Block {i} mismatch in {Path.GetFileName(csoPath)}"
                );
            }

            cso.Dispose();
        }
    }

    [Fact]
    public void CsoStreamReadMatchesIsoContent()
    {
        if (!SamplesExist())
            return;

        var pairs = GetCsoWithIsoPairs().ToArray();
        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var csoPath = pair[0];
            var isoPath = pair[1];

            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);

            using var csoStream = cso.OpenStream();
            using var isoStream = File.OpenRead(isoPath);

            Assert.Equal(isoStream.Length, csoStream.Length);

            var blockSize = (int)cso.Header.BlockSize;
            var csoBuffer = new byte[blockSize];
            var isoBuffer = new byte[blockSize];

            var blocksToCheck = Math.Min(cso.Header.TotalBlocks, 100u);
            for (uint i = 0; i < blocksToCheck; i++)
            {
                var csoRead = csoStream.Read(csoBuffer, 0, blockSize);
                Assert.Equal(blockSize, csoRead);

                var isoRead = isoStream.Read(isoBuffer, 0, blockSize);
                Assert.Equal(blockSize, isoRead);

                Assert.True(
                    csoBuffer.AsSpan().SequenceEqual(isoBuffer.AsSpan()),
                    $"Stream block {i} mismatch in {Path.GetFileName(csoPath)}"
                );
            }

            cso.Dispose();
        }
    }

    [Fact]
    public void CsoStreamSeekAndReadWorks()
    {
        if (!SamplesExist())
            return;

        var csoFiles = Directory.GetFiles(SamplesDir, "*.cso");
        Assert.NotEmpty(csoFiles);

        foreach (var csoPath in csoFiles)
        {
            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);

            using var csoStream = cso.OpenStream();
            var blockSize = (int)cso.Header.BlockSize;

            var block5Buffer = new byte[blockSize];
            csoStream.Position = 5 * blockSize;
            var read = csoStream.Read(block5Buffer, 0, blockSize);
            Assert.Equal(blockSize, read);

            var block5Direct = new byte[blockSize];
            error = cso.ReadBlock(5, block5Direct, out _);
            Assert.Equal(CsoError.None, error);

            Assert.True(
                block5Buffer.AsSpan().SequenceEqual(block5Direct.AsSpan()),
                $"Seek-read mismatch for block 5 in {Path.GetFileName(csoPath)}"
            );

            cso.Dispose();
        }
    }

    [Fact]
    public void ExtractToIsoProducesCorrectFile()
    {
        if (!SamplesExist())
            return;

        var pairs = GetCsoWithIsoPairs().ToArray();
        Assert.NotEmpty(pairs);

        var pair = pairs[0];
        var csoPath = pair[0];
        var isoPath = pair[1];

        var error = CsoFile.Open(csoPath, out var cso);
        Assert.Equal(CsoError.None, error);
        Assert.NotNull(cso);

        var outputPath = Path.Combine(_tempDir, "extracted.iso");
        var progressCalls = new List<(uint Processed, uint Total)>();

        error = cso.ExtractToIso(
            outputPath,
            (processed, total) => progressCalls.Add((processed, total))
        );
        Assert.Equal(CsoError.None, error);
        Assert.True(File.Exists(outputPath));
        Assert.NotEmpty(progressCalls);
        Assert.Equal(cso.Header.TotalBlocks, progressCalls.Last().Total);

        var originalSize = new FileInfo(isoPath).Length;
        var extractedSize = new FileInfo(outputPath).Length;
        Assert.Equal(originalSize, extractedSize);

        using var originalStream = File.OpenRead(isoPath);
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
                $"Extracted file differs at offset {offset}"
            );
            offset += bytesRead1;
        }

        cso.Dispose();
    }

    [Fact]
    public void CsoUncompressedSizeMatchesIsoSize()
    {
        if (!SamplesExist())
            return;

        var pairs = GetCsoWithIsoPairs().ToArray();
        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var csoPath = pair[0];
            var isoPath = pair[1];

            var error = CsoFile.Open(csoPath, out var cso);
            Assert.Equal(CsoError.None, error);
            Assert.NotNull(cso);

            var isoSize = new FileInfo(isoPath).Length;
            Assert.Equal((long)cso.Header.UncompressedSize, isoSize);

            cso.Dispose();
        }
    }
}
