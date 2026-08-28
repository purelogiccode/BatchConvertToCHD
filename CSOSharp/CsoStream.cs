using System.Buffers;
using CSOSharp.Models;

namespace CSOSharp;

/// <summary>
///     A read-only <see cref="Stream" /> that provides sequential access to the decompressed ISO data
///     within a CSO/CISO file. Supports seeking.
/// </summary>
public sealed class CsoStream : Stream
{
    private readonly byte[] _blockBuffer;
    private readonly CsoFile _csoFile;

    /// <summary>
    ///     The current block index loaded into the buffer.
    /// </summary>
    private uint _currentBlockIndex;

    /// <summary>
    ///     Whether the current block buffer contains valid data.
    /// </summary>
    private bool _currentBlockValid;

    private bool _disposed;
    private long _position;

    internal CsoStream(CsoFile csoFile)
    {
        _csoFile = csoFile ?? throw new ArgumentNullException(nameof(csoFile));
        _blockBuffer = ArrayPool<byte>.Shared.Rent((int)csoFile.Header.BlockSize);
        _currentBlockIndex = uint.MaxValue;
        _currentBlockValid = false;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => (long)_csoFile.Header.UncompressedSize;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);

            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || buffer.Length - offset < count)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (count == 0 || _position >= Length)
            return 0;

        var blockSize = (int)_csoFile.Header.BlockSize;
        var totalRead = 0;

        while (totalRead < count && _position < Length)
        {
            var blockIndex = (uint)(_position / blockSize);
            var blockOffset = (int)(_position % blockSize);

            var error = EnsureBlockLoaded(blockIndex);
            if (error != CsoError.None)
                throw new IOException($"Failed to read CSO block {blockIndex}: {error}");

            var available = Math.Min(blockSize - blockOffset, count - totalRead);
            var remainingInFile = Length - _position;
            if (available > remainingInFile) available = (int)remainingInFile;

            Buffer.BlockCopy(_blockBuffer, blockOffset, buffer, offset + totalRead, available);

            totalRead += available;
            _position += available;
        }

        return totalRead;
    }

#if NETCOREAPP
    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty || _position >= Length)
            return 0;

        var blockSize = (int)_csoFile.Header.BlockSize;
        var totalRead = 0;

        while (totalRead < buffer.Length && _position < Length)
        {
            var blockIndex = (uint)(_position / blockSize);
            var blockOffset = (int)(_position % blockSize);

            var error = EnsureBlockLoaded(blockIndex);
            if (error != CsoError.None)
                throw new IOException($"Failed to read CSO block {blockIndex}: {error}");

            var available = Math.Min(blockSize - blockOffset, buffer.Length - totalRead);
            var remainingInFile = Length - _position;
            if (available > remainingInFile) available = (int)remainingInFile;

            _blockBuffer.AsSpan(blockOffset, available).CopyTo(buffer[totalRead..]);

            totalRead += available;
            _position += available;
        }

        return totalRead;
    }
#endif

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentException("Invalid SeekOrigin.", nameof(origin))
        };

        if (newPosition < 0)
            throw new IOException("Cannot seek to a negative position.");

        _position = newPosition;
        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only stream, nothing to flush.
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException("CsoStream is read-only.");
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("CsoStream is read-only.");
    }

    private CsoError EnsureBlockLoaded(uint blockIndex)
    {
        if (_currentBlockValid && _currentBlockIndex == blockIndex)
            return CsoError.None;

        var error = _csoFile.ReadBlock(blockIndex, _blockBuffer, out _);
        if (error != CsoError.None)
        {
            _currentBlockValid = false;
            _currentBlockIndex = uint.MaxValue;
            return error;
        }

        _currentBlockIndex = blockIndex;
        _currentBlockValid = true;
        return CsoError.None;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing) ArrayPool<byte>.Shared.Return(_blockBuffer);

        base.Dispose(disposing);
    }
}