using CSOSharp;
using CSOSharp.Models;

namespace BatchConvertToCHD.Tests;

public class CsoFileTests : IDisposable
{
    private readonly string _tempDir;

    public CsoFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CsoFileTests_{Guid.NewGuid():N}");
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

    [Fact]
    public void OpenNonExistentFileReturnsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "nonexistent.cso");
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.FileNotFound, error);
        Assert.Null(cso);
    }

    [Fact]
    public void OpenEmptyFileReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "empty.cso");
        File.WriteAllBytes(path, []);
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.InvalidHeader, error);
        Assert.Null(cso);
    }

    [Fact]
    public void OpenFileWithWrongMagicReturnsInvalidHeader()
    {
        var path = Path.Combine(_tempDir, "wrongmagic.cso");
        var data = new byte[24 + 4]; // header + 1 index entry
        // Write wrong magic
        BitConverter.GetBytes(0x12345678u).CopyTo(data, 0);
        BitConverter.GetBytes(24u).CopyTo(data, 4); // header size
        BitConverter.GetBytes(2048UL).CopyTo(data, 8); // uncompressed size
        BitConverter.GetBytes(2048u).CopyTo(data, 16); // block size
        data[20] = 1; // version
        data[21] = 0; // index offset shift
        File.WriteAllBytes(path, data);
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.InvalidHeader, error);
        Assert.Null(cso);
    }

    [Fact]
    public void OpenFileWithZeroBlockSizeReturnsInvalidBlockSize()
    {
        var path = Path.Combine(_tempDir, "zeroblock.cso");
        var data = new byte[24 + 4];
        BitConverter.GetBytes(CsoHeader.MagicValue).CopyTo(data, 0);
        BitConverter.GetBytes(24u).CopyTo(data, 4);
        BitConverter.GetBytes(2048UL).CopyTo(data, 8);
        BitConverter.GetBytes(0u).CopyTo(data, 16); // zero block size
        data[20] = 1;
        data[21] = 0;
        File.WriteAllBytes(path, data);
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.InvalidBlockSize, error);
        Assert.Null(cso);
    }

    [Fact]
    public void OpenFileWithUnsupportedVersionReturnsUnsupportedVersion()
    {
        var path = Path.Combine(_tempDir, "badversion.cso");
        var data = new byte[24 + 4];
        BitConverter.GetBytes(CsoHeader.MagicValue).CopyTo(data, 0);
        BitConverter.GetBytes(24u).CopyTo(data, 4);
        BitConverter.GetBytes(2048UL).CopyTo(data, 8);
        BitConverter.GetBytes(2048u).CopyTo(data, 16);
        data[20] = 3; // unsupported version
        data[21] = 0;
        File.WriteAllBytes(path, data);
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.UnsupportedVersion, error);
        Assert.Null(cso);
    }

    [Fact]
    public void OpenValidCsoV1HeaderReturnsSuccess()
    {
        var path = CreateMinimalCsoFile(1);
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.None, error);
        Assert.NotNull(cso);
        Assert.True(cso.Header.IsValid);
        Assert.True(cso.IsDeflate);
        Assert.False(cso.IsLz4);
        cso.Dispose();
    }

    [Fact]
    public void OpenValidCsoV2HeaderReturnsSuccess()
    {
        var path = CreateMinimalCsoFile(2);
        var error = CsoFile.Open(path, out var cso);
        Assert.Equal(CsoError.None, error);
        Assert.NotNull(cso);
        Assert.True(cso.Header.IsValid);
        Assert.False(cso.IsDeflate);
        Assert.True(cso.IsLz4);
        cso.Dispose();
    }

    [Fact]
    public void ReadBlockAfterDisposeReturnsIoError()
    {
        var path = CreateMinimalCsoFile(1);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);
        cso.Dispose();

        var buffer = new byte[cso.Header.BlockSize];
        var error = cso.ReadBlock(0, buffer, out var bytesRead);
        Assert.Equal(CsoError.IoError, error);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ReadBlockOutOfRangeReturnsBlockOutOfRange()
    {
        var path = CreateMinimalCsoFile(1);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        var buffer = new byte[cso.Header.BlockSize];
        var error = cso.ReadBlock(999, buffer, out var bytesRead);
        Assert.Equal(CsoError.BlockOutOfRange, error);
        Assert.Equal(0, bytesRead);
        cso.Dispose();
    }

    [Fact]
    public void OpenStreamAfterDisposeThrowsObjectDisposedException()
    {
        var path = CreateMinimalCsoFile(1);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);
        cso.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cso.OpenStream());
    }

    [Fact]
    public void ExtractToIsoAfterDisposeThrowsObjectDisposedException()
    {
        var path = CreateMinimalCsoFile(1);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);
        cso.Dispose();

        var outputPath = Path.Combine(_tempDir, "output.iso");
        Assert.Throws<ObjectDisposedException>(() => cso.ExtractToIso(outputPath));
    }

    [Fact]
    public void DisposeMultipleTimesDoesNotThrow()
    {
        var path = CreateMinimalCsoFile(1);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        cso.Dispose();
        var exception = Record.Exception(() => cso.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void OpenStreamReturnsCsoStream()
    {
        var path = CreateMinimalCsoFile(1);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        using var stream = cso.OpenStream();
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        cso.Dispose();
    }

    private string CreateMinimalCsoFile(byte version, string extension = ".cso")
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}{extension}");
        const uint blockSize = 2048u;
        const uint totalBlocks = 1u;
        const ulong uncompressedSize = blockSize * totalBlocks;
        const uint indexEntries = totalBlocks + 1; // need N+1 entries

        using var ms = new MemoryStream();

        // Header (24 bytes)
        ms.Write(BitConverter.GetBytes(CsoHeader.MagicValue));
        ms.Write(BitConverter.GetBytes(24u)); // header size
        ms.Write(BitConverter.GetBytes(uncompressedSize));
        ms.Write(BitConverter.GetBytes(blockSize));
        ms.WriteByte(version);
        ms.WriteByte(0); // index offset shift
        ms.Write(new byte[2]); // padding

        // Index table: all blocks uncompressed, pointing to data right after index
        const uint dataOffset = 24 + indexEntries * 4;
        for (var i = 0; i < indexEntries; i++)
        {
            // Set high bit to indicate uncompressed
            ms.Write(BitConverter.GetBytes(dataOffset | 0x80000000u));
        }

        // Data: one block of zeros
        ms.Write(new byte[blockSize]);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}