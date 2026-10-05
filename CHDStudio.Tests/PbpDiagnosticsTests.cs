using PBPSharp;
using PBPSharp.Models;

namespace CHDStudio.Tests;

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

    [Fact]
    public void OpenStreamReadFailureRecordsDetailAndReturnsIoError()
    {
        PbpDiagnostics.TakeDetail();

        using var stream = new ThrowingReadStream();
        var error = PbpFile.Open(stream, false, out var pbpFile);

        Assert.Equal(PbpError.IoError, error);
        Assert.Null(pbpFile);

        var detail = PbpDiagnostics.TakeDetail();
        Assert.NotNull(detail);
        Assert.Contains("Simulated disk error", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenLockedFileRecordsDetailAndReturnsIoError()
    {
        PbpDiagnostics.TakeDetail();

        var pbpPath = Path.Combine(_tempDir, "locked.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(pbpPath);

        using var exclusive = new FileStream(
            pbpPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );
        var error = PbpFile.Open(pbpPath, out var pbpFile);

        Assert.Equal(PbpError.IoError, error);
        Assert.Null(pbpFile);

        var detail = PbpDiagnostics.TakeDetail();
        Assert.NotNull(detail);
        Assert.Contains("Failed to open", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractionToUnwritablePathRecordsDetailAndReturnsIoError()
    {
        PbpDiagnostics.TakeDetail();

        var pbpPath = Path.Combine(_tempDir, "valid.pbp");
        new PbpTestFileBuilder().WithBlockCount(2).BuildTo(pbpPath);

        var openError = PbpFile.Open(pbpPath, out var pbpFile);
        Assert.Equal(PbpError.None, openError);

        using (pbpFile)
        {
            var binPath = Path.Combine(_tempDir, "missing-subdirectory", "out.bin");
            var error = pbpFile!.Discs[0]
                .ExtractToBinCue(binPath, null, null, CancellationToken.None);

            Assert.Equal(PbpError.IoError, error);
        }

        var detail = PbpDiagnostics.TakeDetail();
        Assert.NotNull(detail);
        Assert.Contains("I/O error while extracting disc 1", detail, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A readable, seekable stream whose reads always fail with an <see cref="IOException" />,
    ///     standing in for a failing disk during
    ///     <see cref="PbpFile.Open(Stream, bool, out PbpFile?)" />.
    /// </summary>
    private sealed class ThrowingReadStream : Stream
    {
        /// <summary>Always true: the stream advertises itself as readable so Open proceeds to read.</summary>
        public override bool CanRead => true;

        /// <summary>Always true: the stream advertises itself as seekable so Open accepts it.</summary>
        public override bool CanSeek => true;

        /// <summary>Always false: the test stream is read-only.</summary>
        public override bool CanWrite => false;

        /// <summary>The stream length, reported as zero because reads always fail.</summary>
        public override long Length => 0;

        /// <summary>The current position, pinned to zero because reads always fail.</summary>
        public override long Position
        {
            get => 0;
            set { }
        }

        /// <summary>No-op flush; the stream holds no buffered data.</summary>
        public override void Flush() { }

        /// <summary>Throws the simulated disk failure for every read.</summary>
        /// <param name="buffer">Destination buffer (unused).</param>
        /// <param name="offset">Destination offset (unused).</param>
        /// <param name="count">Byte count (unused).</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="IOException">Always thrown.</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("Simulated disk error.");
        }

        /// <summary>Accepts any seek and reports position zero.</summary>
        /// <param name="offset">Seek offset (unused).</param>
        /// <param name="origin">Seek origin (unused).</param>
        /// <returns>Always zero.</returns>
        public override long Seek(long offset, SeekOrigin origin)
        {
            return 0;
        }

        /// <summary>No-op: the test stream has no real length.</summary>
        /// <param name="value">Requested length (unused).</param>
        public override void SetLength(long value) { }

        /// <summary>No-op: the test stream is read-only.</summary>
        /// <param name="buffer">Source buffer (unused).</param>
        /// <param name="offset">Source offset (unused).</param>
        /// <param name="count">Byte count (unused).</param>
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}