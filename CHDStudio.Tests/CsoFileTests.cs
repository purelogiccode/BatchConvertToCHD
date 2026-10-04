using CSOSharp;
using CSOSharp.Models;

namespace CHDStudio.Tests;

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

    [Fact]
    public void ReadBlockV2StoredBlockReturnsData()
    {
        var raw = new byte[2048];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 251);

        var path = CreateSingleBlockCsoFile(2, (ulong)raw.Length, raw, compressed: false, lz4: false);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        var buffer = new byte[2048];
        var error = cso.ReadBlock(0, buffer, out var bytesRead);

        Assert.Equal(CsoError.None, error);
        Assert.Equal(2048, bytesRead);
        Assert.Equal(raw, buffer);
        cso.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadBlockV2CompressedBlockDecodes(bool lz4)
    {
        var raw = new byte[2048];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 7);

        var path = CreateSingleBlockCsoFile(2, (ulong)raw.Length, raw, compressed: true, lz4: lz4);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        var buffer = new byte[2048];
        var error = cso.ReadBlock(0, buffer, out var bytesRead);

        Assert.Equal(CsoError.None, error);
        Assert.Equal(2048, bytesRead);
        Assert.Equal(raw, buffer);
        cso.Dispose();
    }

    [Fact]
    public void ReadBlockV1ShortFinalStoredBlockZeroPads()
    {
        var raw = new byte[100];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(i + 1);

        var path = CreateSingleBlockCsoFile(1, (ulong)raw.Length, raw, compressed: false, lz4: false);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        var buffer = new byte[2048];
        var error = cso.ReadBlock(0, buffer, out var bytesRead);

        Assert.Equal(CsoError.None, error);
        Assert.Equal(2048, bytesRead);
        Assert.Equal(raw, buffer[..raw.Length]);
        Assert.All(buffer[raw.Length..], static b => Assert.Equal(0, b));

        var outputPath = Path.Combine(_tempDir, "short.iso");
        Assert.Equal(CsoError.None, cso.ExtractToIso(outputPath));
        Assert.Equal(raw.Length, new FileInfo(outputPath).Length);
        cso.Dispose();
    }

    [Fact]
    public void ExtractToIsoZeroUncompressedSizeReturnsInvalidHeader()
    {
        // A header declaring no data must not "succeed" by writing an empty ISO.
        var raw = new byte[2048];
        var path = CreateSingleBlockCsoFile(1, 0, raw, compressed: false, lz4: false);
        CsoFile.Open(path, out var cso);
        Assert.NotNull(cso);

        var outputPath = Path.Combine(_tempDir, "empty.iso");
        Assert.Equal(CsoError.InvalidHeader, cso.ExtractToIso(outputPath));
        cso.Dispose();
    }

    /// <summary>
    ///     Builds a one-block CSO. <paramref name="compressed" /> writes the block through LZ4 or
    ///     deflate; otherwise it is stored. v2 marks LZ4 blocks with the high index bit and uses the
    ///     block size to tell stored blocks apart, so the fixture follows that convention.
    /// </summary>
    /// <param name="version">CSO format version.</param>
    /// <param name="uncompressedSize">Declared uncompressed size.</param>
    /// <param name="raw">The block's uncompressed bytes.</param>
    /// <param name="compressed">Whether the block is compressed.</param>
    /// <param name="lz4">Whether a compressed block uses LZ4 (otherwise deflate).</param>
    /// <returns>Path of the written CSO file.</returns>
    private string CreateSingleBlockCsoFile(
        byte version,
        ulong uncompressedSize,
        byte[] raw,
        bool compressed,
        bool lz4
    )
    {
        const uint blockSize = 2048u;
        const uint dataOffset = 24 + 2 * 4;

        var data = raw;
        var firstEntry = dataOffset;
        if (compressed)
        {
            data = lz4 ? Lz4Compress(raw) : Deflate(raw);
            if (version == 2 && lz4) firstEntry |= 0x80000000u;
        }
        else if (version == 1)
        {
            firstEntry |= 0x80000000u;
        }

        var path = Path.Combine(_tempDir, $"single_{Guid.NewGuid():N}.cso");
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(CsoHeader.MagicValue));
        ms.Write(BitConverter.GetBytes(24u));
        ms.Write(BitConverter.GetBytes(uncompressedSize));
        ms.Write(BitConverter.GetBytes(blockSize));
        ms.WriteByte(version);
        ms.WriteByte(0);
        ms.Write(new byte[2]);
        ms.Write(BitConverter.GetBytes(firstEntry));
        ms.Write(BitConverter.GetBytes(dataOffset + (uint)data.Length));
        ms.Write(data);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>Compresses <paramref name="raw" /> as an LZ4 block.</summary>
    /// <param name="raw">Bytes to compress.</param>
    /// <returns>The compressed bytes.</returns>
    private static byte[] Lz4Compress(byte[] raw)
    {
        var target = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(raw.Length)];
        var size = K4os.Compression.LZ4.LZ4Codec.Encode(
            raw,
            target.AsSpan(),
            K4os.Compression.LZ4.LZ4Level.L00_FAST
        );
        return target[..size];
    }

    /// <summary>Compresses <paramref name="raw" /> as a raw deflate stream.</summary>
    /// <param name="raw">Bytes to compress.</param>
    /// <returns>The compressed bytes.</returns>
    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
            output,
            System.IO.Compression.CompressionMode.Compress,
            true
        ))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
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
            // v1 marks a stored block with the high bit; v2 stores a block when its size is a full
            // block, so no flag is used there.
            var entry = version == 1 ? dataOffset | 0x80000000u : dataOffset;
            ms.Write(BitConverter.GetBytes(entry));
        }

        // Data: one block of zeros
        ms.Write(new byte[blockSize]);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}