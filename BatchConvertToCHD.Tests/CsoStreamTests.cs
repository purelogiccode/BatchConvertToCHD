using CSOSharp;
using CSOSharp.Models;

namespace BatchConvertToCHD.Tests;

public class CsoStreamTests : IDisposable
{
    private readonly string _tempDir;

    public CsoStreamTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CsoStreamTests_{Guid.NewGuid():N}");
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
    public void StreamPropertiesAreCorrect()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(2048L, stream.Length);
        Assert.Equal(0L, stream.Position);
    }

    [Fact]
    public void ReadZeroBytesReturnsZero()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();

        var buffer = new byte[10];
        var read = stream.Read(buffer, 0, 0);
        Assert.Equal(0, read);
        cso.Dispose();
    }

    [Fact]
    public void ReadBeyondLengthReturnsZero()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();
        stream.Position = stream.Length;

        var buffer = new byte[10];
        var read = stream.Read(buffer, 0, 10);
        Assert.Equal(0, read);
        cso.Dispose();
    }

    [Fact]
    public void ReadReturnsExpectedData()
    {
        var expectedData = new byte[2048];
        for (var i = 0; i < expectedData.Length; i++) expectedData[i] = (byte)(i % 256);

        var cso = CreateCsoWithData(expectedData, 1);
        using var stream = cso.OpenStream();

        var buffer = new byte[2048];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
                break;

            totalRead += read;
        }

        Assert.Equal(2048, totalRead);
        Assert.Equal(expectedData, buffer);
        cso.Dispose();
    }

    [Fact]
    public void SeekBeginSetsPosition()
    {
        var cso = CreateCsoWithBlocks(2, 2048, 1);
        using var stream = cso.OpenStream();

        var pos = stream.Seek(1000, SeekOrigin.Begin);
        Assert.Equal(1000L, pos);
        Assert.Equal(1000L, stream.Position);
        cso.Dispose();
    }

    [Fact]
    public void SeekCurrentAdjustsPosition()
    {
        var cso = CreateCsoWithBlocks(2, 2048, 1);
        using var stream = cso.OpenStream();
        stream.Position = 500;

        var pos = stream.Seek(200, SeekOrigin.Current);
        Assert.Equal(700L, pos);
        Assert.Equal(700L, stream.Position);
        cso.Dispose();
    }

    [Fact]
    public void SeekEndSetsPositionFromEnd()
    {
        var cso = CreateCsoWithBlocks(2, 2048, 1);
        using var stream = cso.OpenStream();

        var pos = stream.Seek(-100, SeekOrigin.End);
        Assert.Equal(stream.Length - 100, pos);
        Assert.Equal(stream.Length - 100, stream.Position);
        cso.Dispose();
    }

    [Fact]
    public void SeekNegativeThrowsIoException()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();

        Assert.Throws<IOException>(() => stream.Seek(-9999, SeekOrigin.Begin));
        cso.Dispose();
    }

    [Fact]
    public void SetLengthThrowsNotSupportedException()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();

        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
        cso.Dispose();
    }

    [Fact]
    public void WriteThrowsNotSupportedException()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();

        Assert.Throws<NotSupportedException>(() => stream.Write([1, 2, 3], 0, 3));
        cso.Dispose();
    }

    [Fact]
    public void FlushDoesNotThrow()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        using var stream = cso.OpenStream();

        var exception = Record.Exception(stream.Flush);
        Assert.Null(exception);
        cso.Dispose();
    }

    [Fact]
    public void ReadAfterDisposeThrowsObjectDisposedException()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        var stream = cso.OpenStream();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[10], 0, 10));
        cso.Dispose();
    }

    [Fact]
    public void SeekAfterDisposeThrowsObjectDisposedException()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        var stream = cso.OpenStream();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        cso.Dispose();
    }

    [Fact]
    public void PositionSetAfterDisposeThrowsObjectDisposedException()
    {
        var cso = CreateCsoWithBlocks(1, 2048, 1);
        var stream = cso.OpenStream();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        cso.Dispose();
    }

    [Fact]
    public void ReadAcrossBlockBoundaryReturnsCorrectData()
    {
        // Create a 2-block CSO with known data
        var data = new byte[4096];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var cso = CreateCsoWithData(data, 1);
        using var stream = cso.OpenStream();

        // Read starting near the end of first block
        stream.Position = 2040;
        var buffer = new byte[20]; // crosses boundary at 2048
        var read = stream.Read(buffer, 0, 20);

        Assert.Equal(20, read);
        // First 8 bytes should be from block 0 (offset 2040-2047)
        for (var i = 0; i < 8; i++)
            Assert.Equal((byte)((2040 + i) % 256), buffer[i]);
        // Next 12 bytes should be from block 1 (offset 0-11)
        for (var i = 0; i < 12; i++)
            Assert.Equal((byte)(i % 256), buffer[8 + i]);
        cso.Dispose();
    }

    [Fact]
    public void ReadSpanReturnsCorrectData()
    {
        var data = new byte[2048];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var cso = CreateCsoWithData(data, 1);
        using var stream = cso.OpenStream();

        var buffer = new byte[2048];
        var read = stream.Read(buffer.AsSpan());

        Assert.Equal(2048, read);
        Assert.Equal(data, buffer);
        cso.Dispose();
    }

    [Fact]
    public void SeekAndReadMultipleBlocksWorks()
    {
        var data = new byte[6144]; // 3 blocks
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var cso = CreateCsoWithData(data, 1);
        using var stream = cso.OpenStream();

        // Seek to block 2
        stream.Position = 4096;
        var buffer = new byte[2048];
        var read = stream.Read(buffer, 0, 2048);

        Assert.Equal(2048, read);
        for (var i = 0; i < 2048; i++)
            Assert.Equal((byte)((4096 + i) % 256), buffer[i]);
        cso.Dispose();
    }

    private CsoFile CreateCsoWithBlocks(uint blockCount, uint blockSize, byte version)
    {
        var data = new byte[blockSize * blockCount];
        return CreateCsoWithData(data, version);
    }

    private CsoFile CreateCsoWithData(byte[] uncompressedData, byte version)
    {
        const uint blockSize = 2048u;
        var totalBlocks = (uint)(uncompressedData.Length / blockSize);
        var indexEntries = totalBlocks + 1;

        using var ms = new MemoryStream();

        // Header
        ms.Write(BitConverter.GetBytes(CsoHeader.MagicValue));
        ms.Write(BitConverter.GetBytes(24u));
        ms.Write(BitConverter.GetBytes((ulong)uncompressedData.Length));
        ms.Write(BitConverter.GetBytes(blockSize));
        ms.WriteByte(version);
        ms.WriteByte(0);
        ms.Write(new byte[2]);

        // Data starts right after index
        var dataStart = 24 + indexEntries * 4;

        // For simplicity, store all blocks as uncompressed
        for (var i = 0; i < indexEntries; i++)
            ms.Write(BitConverter.GetBytes((dataStart + (uint)(i * blockSize)) | 0x80000000u));

        // Write the actual data
        ms.Write(uncompressedData);

        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.cso");
        File.WriteAllBytes(path, ms.ToArray());

        CsoFile.Open(path, out var cso);
        return cso!;
    }
}