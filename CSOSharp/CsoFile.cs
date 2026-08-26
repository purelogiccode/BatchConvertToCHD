using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using CSOSharp.Models;
using K4os.Compression.LZ4;

namespace CSOSharp;

/// <summary>
/// Provides functionality to open and read CSO/CISO (Compressed ISO) files.
/// Supports CSO v1 (deflate/zlib) and CSO v2/ZSO (LZ4) formats.
/// </summary>
public sealed class CsoFile : IDisposable
{
    private Stream _stream;
    private readonly bool _ownsStream;
    private bool _disposed;
    private uint[] _indexTable;

    /// <summary>
    /// The parsed CSO header containing format metadata.
    /// </summary>
    public CsoHeader Header { get; }

    /// <summary>
    /// Whether this CSO file uses LZ4 compression (CSO v2/ZSO).
    /// </summary>
    public bool IsLz4 => Header.IsV2;

    /// <summary>
    /// Whether this CSO file uses deflate/zlib compression (CSO v1).
    /// </summary>
    public bool IsDeflate => Header.IsV1;

    private CsoFile(Stream stream, bool ownsStream, CsoHeader header, uint[] indexTable)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        _indexTable = indexTable;
    }

    /// <summary>
    /// Opens a CSO/CISO file from the specified file path.
    /// </summary>
    /// <param name="path">The full path to the CSO or CISO file.</param>
    /// <param name="cso">When this method returns, contains the opened <see cref="CsoFile"/> instance if successful; otherwise, null.</param>
    /// <returns>A <see cref="CsoError"/> indicating the result of the operation.</returns>
    public static CsoError Open(string path, out CsoFile? cso)
    {
        cso = null;

        if (!File.Exists(path))
            return CsoError.FileNotFound;

        try
        {
            var stream = File.OpenRead(path);
            var error = Open(stream, true, out cso);
            if (error != CsoError.None)
            {
                stream.Dispose();
            }

            return error;
        }
        catch (IOException)
        {
            return CsoError.IoError;
        }
    }

    /// <summary>
    /// Opens a CSO/CISO from an existing stream.
    /// </summary>
    /// <param name="stream">The stream containing CSO data. The stream must be seekable and readable.</param>
    /// <param name="ownsStream">Whether this instance should dispose the stream when disposed.</param>
    /// <param name="cso">When this method returns, contains the opened <see cref="CsoFile"/> instance if successful; otherwise, null.</param>
    /// <returns>A <see cref="CsoError"/> indicating the result of the operation.</returns>
    public static CsoError Open(Stream stream, bool ownsStream, out CsoFile? cso)
    {
        cso = null;

        if (stream is not { CanRead: true } || !stream.CanSeek)
            return CsoError.IoError;

        try
        {
            stream.Seek(0, SeekOrigin.Begin);

            var headerError = ReadHeader(stream, out var header);
            if (headerError != CsoError.None)
                return headerError;

            var indexError = ReadIndexTable(stream, header, out var indexTable);
            if (indexError != CsoError.None)
                return indexError;

            cso = new CsoFile(stream, ownsStream, header, indexTable);
            return CsoError.None;
        }
        catch (IOException)
        {
            return CsoError.IoError;
        }
    }

    private static CsoError ReadHeader(Stream stream, out CsoHeader header)
    {
        header = default;

        Span<byte> headerBytes = stackalloc byte[CsoHeader.ExpectedHeaderSize];
        if (stream.Read(headerBytes) != CsoHeader.ExpectedHeaderSize)
            return CsoError.InvalidHeader;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[..4]);
        if (magic != CsoHeader.MagicValue)
            return CsoError.InvalidHeader;

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[4..8]);
        var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes[8..16]);
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[16..20]);
        var version = headerBytes[20];
        var indexOffsetShift = headerBytes[21];

        if (blockSize == 0)
            return CsoError.InvalidBlockSize;

        if (version != 1 && version != 2)
            return CsoError.UnsupportedVersion;

        header = new CsoHeader(
            magic,
            headerSize,
            uncompressedSize,
            blockSize,
            version,
            indexOffsetShift
        );
        return CsoError.None;
    }

    private static CsoError ReadIndexTable(Stream stream, CsoHeader header, out uint[] indexTable)
    {
        indexTable = [];

        var totalEntries = header.TotalBlocks + 1;

        indexTable = new uint[totalEntries];

        Span<byte> indexBytes = stackalloc byte[4];
        for (uint i = 0; i < totalEntries; i++)
        {
            if (stream.Read(indexBytes) != 4)
                return CsoError.CorruptIndex;

            indexTable[i] = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes);
        }

        return CsoError.None;
    }

    /// <summary>
    /// Reads and decompresses a single block from the CSO file.
    /// </summary>
    /// <param name="blockIndex">The zero-based index of the block to read.</param>
    /// <param name="buffer">The buffer to receive the decompressed block data. Must be at least <see cref="CsoHeader.BlockSize"/> bytes.</param>
    /// <param name="bytesRead">When this method returns, contains the number of bytes actually read into the buffer.</param>
    /// <returns>A <see cref="CsoError"/> indicating the result of the operation.</returns>
    public CsoError ReadBlock(uint blockIndex, byte[] buffer, out int bytesRead)
    {
        return ReadBlock(blockIndex, buffer, 0, out bytesRead);
    }

    /// <summary>
    /// Reads and decompresses a single block from the CSO file into the specified buffer at the given offset.
    /// </summary>
    /// <param name="blockIndex">The zero-based index of the block to read.</param>
    /// <param name="buffer">The buffer to receive the decompressed block data.</param>
    /// <param name="offset">The byte offset in the buffer at which to begin writing.</param>
    /// <param name="bytesRead">When this method returns, contains the number of bytes actually read into the buffer.</param>
    /// <returns>A <see cref="CsoError"/> indicating the result of the operation.</returns>
    public CsoError ReadBlock(uint blockIndex, byte[] buffer, int offset, out int bytesRead)
    {
        bytesRead = 0;

        if (_disposed)
            return CsoError.IoError;

        if (blockIndex >= Header.TotalBlocks)
            return CsoError.BlockOutOfRange;

        if (buffer.Length - offset < Header.BlockSize)
            return CsoError.DecompressionError;

        try
        {
            var currentEntry = _indexTable[blockIndex];
            var nextEntry = _indexTable[blockIndex + 1];

            var isUncompressed = (currentEntry & 0x80000000) != 0;
            var rawOffset = (long)(currentEntry & 0x7FFFFFFF) << Header.IndexOffsetShift;
            var nextRawOffset = (long)(nextEntry & 0x7FFFFFFF) << Header.IndexOffsetShift;
            var compressedSize = (int)(nextRawOffset - rawOffset);

            if (compressedSize <= 0)
            {
                Array.Clear(buffer, offset, (int)Header.BlockSize);
                bytesRead = (int)Header.BlockSize;
                return CsoError.None;
            }

            _stream.Seek(rawOffset, SeekOrigin.Begin);

            if (isUncompressed)
            {
                bytesRead = _stream.Read(buffer, offset, (int)Header.BlockSize);
                return CsoError.None;
            }

            if (Header.IsV2)
            {
                return DecompressLz4Block(compressedSize, buffer, offset, out bytesRead);
            }

            return DecompressDeflateBlock(compressedSize, buffer, offset, out bytesRead);
        }
        catch (IOException)
        {
            return CsoError.IoError;
        }
        catch (Exception)
        {
            return CsoError.DecompressionError;
        }
    }

    private CsoError DecompressDeflateBlock(
        int compressedSize,
        byte[] buffer,
        int offset,
        out int bytesRead
    )
    {
        bytesRead = 0;

        var compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedSize);
        try
        {
            var actuallyRead = _stream.Read(compressedBuffer, 0, compressedSize);
            if (actuallyRead != compressedSize)
                return CsoError.IoError;

            var dataOffset = 0;
            if (actuallyRead >= 2 && (compressedBuffer[0] == 0x78))
            {
                dataOffset = 2;
            }

            using var compressedStream = new MemoryStream(
                compressedBuffer,
                dataOffset,
                actuallyRead - dataOffset
            );
            using var deflateStream = new DeflateStream(
                compressedStream,
                CompressionMode.Decompress,
                false
            );

            var totalRead = 0;
            var blockSize = (int)Header.BlockSize;
            while (totalRead < blockSize)
            {
                var read = deflateStream.Read(buffer, offset + totalRead, blockSize - totalRead);
                if (read == 0)
                    break;

                totalRead += read;
            }

            bytesRead = totalRead;
            return CsoError.None;
        }
        catch (InvalidDataException)
        {
            return CsoError.DecompressionError;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedBuffer);
        }
    }

    private CsoError DecompressLz4Block(
        int compressedSize,
        byte[] buffer,
        int offset,
        out int bytesRead
    )
    {
        bytesRead = 0;

        var compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedSize);
        try
        {
            var actuallyRead = _stream.Read(compressedBuffer, 0, compressedSize);
            if (actuallyRead != compressedSize)
                return CsoError.IoError;

            var blockSize = (int)Header.BlockSize;
            var decoded = LZ4Codec.Decode(
                compressedBuffer,
                0,
                actuallyRead,
                buffer,
                offset,
                blockSize
            );

            if (decoded < 0)
                return CsoError.DecompressionError;

            bytesRead = decoded;
            return CsoError.None;
        }
        catch (Exception)
        {
            return CsoError.DecompressionError;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedBuffer);
        }
    }

    /// <summary>
    /// Creates a <see cref="Stream"/> that provides sequential read access to the decompressed ISO data.
    /// </summary>
    /// <returns>A <see cref="CsoStream"/> wrapping this CSO file.</returns>
    public CsoStream OpenStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new CsoStream(this);
    }

    /// <summary>
    /// Decompresses the entire CSO file and writes the ISO to the specified output path.
    /// </summary>
    /// <param name="outputPath">The path where the decompressed ISO file will be written.</param>
    /// <param name="progress">Optional callback invoked with the number of blocks processed and total blocks.</param>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A <see cref="CsoError"/> indicating the result of the operation.</returns>
    public CsoError ExtractToIso(
        string outputPath,
        Action<uint, uint>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = ArrayPool<byte>.Shared.Rent((int)Header.BlockSize);
        try
        {
            using var outputStream = File.Create(outputPath);
            for (uint i = 0; i < Header.TotalBlocks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var error = ReadBlock(i, buffer, out var bytesRead);
                if (error != CsoError.None)
                    return error;

                outputStream.Write(buffer, 0, bytesRead);
                progress?.Invoke(i + 1, Header.TotalBlocks);
            }

            return CsoError.None;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return CsoError.IoError;
        }
        catch (Exception)
        {
            return CsoError.DecompressionError;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Disposes of the CSO file and releases associated resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_ownsStream)
        {
            _stream.Dispose();
        }

        _stream = null!;
        _indexTable = [];
    }
}
