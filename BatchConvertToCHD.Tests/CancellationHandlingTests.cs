using System.Security.Cryptography;

namespace BatchConvertToCHD.Tests;

public class CancellationHandlingTests
{
    [Fact]
    public void IsCancellationException_TaskCanceledException_ReturnsTrue()
    {
        var ex = new TaskCanceledException("A task was canceled.");

        Assert.True(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_OperationCanceledException_ReturnsTrue()
    {
        var ex = new OperationCanceledException("Operation was canceled.");

        Assert.True(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_CanceledCancellationToken_ReturnsTrue()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = Assert.ThrowsAny<OperationCanceledException>(() =>
            cts.Token.ThrowIfCancellationRequested()
        );

        Assert.True(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_InvalidOperationException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Something went wrong.");

        Assert.False(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_IOException_ReturnsFalse()
    {
        var ex = new IOException("Not enough disk space.", -2147024784);

        Assert.False(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsCancellationException_FormatException_ReturnsFalse()
    {
        var ex = new FormatException("Input string was not in a correct format.");

        Assert.False(MainWindow.IsCancellationException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_IoExceptionDiskFull_ReturnsTrue()
    {
        // HResult -2147024784 = 0x80070070 = ERROR_DISK_FULL
        var ex = new IOException("There is not enough space on the disk.", -2147024784);

        Assert.True(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_IoExceptionSemTimeout_ReturnsTrue()
    {
        // HResult -2147024783 = 0x80070079 = ERROR_SEM_TIMEOUT
        var ex = new IOException("The semaphore timeout period has expired.", -2147024783);

        Assert.True(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_IoExceptionOtherHResult_ReturnsFalse()
    {
        var ex = new IOException("File not found.", -2147024894); // 0x80070002 = ERROR_FILE_NOT_FOUND

        Assert.False(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsDiskSpaceException_NonIoException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Disk full"); // wrong exception type

        Assert.False(MainWindow.IsDiskSpaceException(ex));
    }

    [Fact]
    public void IsCancellationException_TaskCanceledException_IsDistinctFromDiskSpace()
    {
        // Verifies that TaskCanceledException does NOT match IsDiskSpaceException,
        // preventing the bug where it was being reported as a disk space error
        var ex = new TaskCanceledException("A task was canceled.");

        Assert.True(MainWindow.IsCancellationException(ex));
        Assert.False(MainWindow.IsDiskSpaceException(ex));
    }

    // --- IsCorruptionException tests ---

    [Fact]
    public void IsCorruptionException_InvalidDataException_ReturnsTrue()
    {
        var ex = new InvalidDataException("archive is corrupt");
        Assert.True(MainWindow.IsCorruptionException(ex));
    }

    [Fact]
    public void IsCorruptionException_IndexOutOfRangeException_ReturnsTrue()
    {
        var ex = new IndexOutOfRangeException("array index out of bounds");
        Assert.True(MainWindow.IsCorruptionException(ex));
    }

    [Fact]
    public void IsCorruptionException_NullReferenceException_ReturnsTrue()
    {
        var ex = new NullReferenceException("null reference");
        Assert.True(MainWindow.IsCorruptionException(ex));
    }

    [Fact]
    public void IsCorruptionException_CryptographicException_ReturnsTrue()
    {
        var ex = new CryptographicException("bad data");
        Assert.True(MainWindow.IsCorruptionException(ex));
    }

    [Fact]
    public void IsCorruptionException_InvalidOperationException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("not a corruption error");
        Assert.False(MainWindow.IsCorruptionException(ex));
    }

    [Fact]
    public void IsCorruptionException_IOException_ReturnsFalse()
    {
        var ex = new IOException("io error");
        Assert.False(MainWindow.IsCorruptionException(ex));
    }

    [Fact]
    public void IsCorruptionException_TaskCanceledException_ReturnsFalse()
    {
        var ex = new TaskCanceledException("cancelled");
        Assert.False(MainWindow.IsCorruptionException(ex));
    }

    // --- IsCrcErrorException tests ---

    [Fact]
    public void IsCrcErrorException_IoExceptionCrcHResult_ReturnsTrue()
    {
        // HResult 0x80070017 = -2147024809 = ERROR_CRC
        var ex = new IOException("Data error (cyclic redundancy check).", -2147024809);
        Assert.True(MainWindow.IsCrcErrorException(ex));
    }

    [Fact]
    public void IsCrcErrorException_IoExceptionOtherHResult_ReturnsFalse()
    {
        var ex = new IOException("some other error", -2147024894); // ERROR_FILE_NOT_FOUND
        Assert.False(MainWindow.IsCrcErrorException(ex));
    }

    [Fact]
    public void IsCrcErrorException_NonIoException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("not a crc error");
        Assert.False(MainWindow.IsCrcErrorException(ex));
    }

    [Fact]
    public void IsCrcErrorException_DoesNotOverlapWithDiskSpace()
    {
        var crcEx = new IOException("CRC error", -2147024809);
        var diskEx = new IOException("disk full", -2147024784);

        Assert.True(MainWindow.IsCrcErrorException(crcEx));
        Assert.False(MainWindow.IsDiskSpaceException(crcEx));

        Assert.True(MainWindow.IsDiskSpaceException(diskEx));
        Assert.False(MainWindow.IsCrcErrorException(diskEx));
    }

    [Fact]
    public void IsCancellationException_IsCorruptionException_MutuallyExclusive()
    {
        var cancelEx = new TaskCanceledException("cancelled");
        var corruptEx = new InvalidDataException("corrupt");

        Assert.True(MainWindow.IsCancellationException(cancelEx));
        Assert.False(MainWindow.IsCorruptionException(cancelEx));

        Assert.True(MainWindow.IsCorruptionException(corruptEx));
        Assert.False(MainWindow.IsCancellationException(corruptEx));
    }
}