using System.Diagnostics.CodeAnalysis;
using BatchConvertToCHD.Diagnostics;

namespace BatchConvertToCHD.Tests;

[SuppressMessage("ReSharper", "AccessToModifiedClosure")]
public class IoThroughputCounterTests
{
    private static bool PlatformSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    [Fact]
    public void CreateForWritesReturnsCounterOnSupportedPlatform()
    {
        using var counter = IoThroughputCounter.CreateForWrites();

        if (!PlatformSupported) Assert.Null(counter);
        else Assert.NotNull(counter);
    }

    [Fact]
    public void CreateForReadsReturnsCounterOnSupportedPlatform()
    {
        using var counter = IoThroughputCounter.CreateForReads();

        if (!PlatformSupported) Assert.Null(counter);
        else Assert.NotNull(counter);
    }

    [Fact]
    public void NextValueFirstCallReturnsZeroBaseline()
    {
        using var counter = new IoThroughputCounter(static () => 1000);

        Assert.Equal(0, counter.NextValue());
    }

    [Fact]
    public void NextValueReturnsPositiveRateWhenBytesIncrease()
    {
        long total = 1000;
        using var counter = new IoThroughputCounter(() => total);

        Assert.Equal(0, counter.NextValue());
        Thread.Sleep(20);
        total += 100_000;

        Assert.True(counter.NextValue() > 0);
    }

    [Fact]
    public void NextValueReturnsZeroWhenBytesUnchanged()
    {
        using var counter = new IoThroughputCounter(static () => 1000);

        counter.NextValue();
        Thread.Sleep(5);

        Assert.Equal(0, counter.NextValue());
    }

    [Fact]
    public void NextValueReturnsZeroWhenBytesDecrease()
    {
        long total = 1000;
        using var counter = new IoThroughputCounter(() => total);

        counter.NextValue();
        Thread.Sleep(5);
        total = 500;

        Assert.Equal(0, counter.NextValue());
    }

    [Fact]
    public void NextValueReturnsZeroWhenSourceReturnsNull()
    {
        using var counter = new IoThroughputCounter(static () => null);

        Assert.Equal(0, counter.NextValue());
        Assert.Equal(0, counter.NextValue());
    }

    [Fact]
    public void NextValueReturnsZeroWhenSourceThrows()
    {
        using var counter = new IoThroughputCounter(
            static () => throw new InvalidOperationException("boom")
        );

        Assert.Equal(0, counter.NextValue());
    }

    [Fact]
    public void NextValueUpdatesBaselineAfterEachCall()
    {
        long total = 1000;
        using var counter = new IoThroughputCounter(() => total);

        counter.NextValue();
        Thread.Sleep(5);
        total += 50_000;
        Assert.True(counter.NextValue() > 0);

        Thread.Sleep(5);
        Assert.Equal(0, counter.NextValue());
    }

    [Fact]
    public void DisposeDoesNotThrow()
    {
        var counter = new IoThroughputCounter(static () => 1000);

        var exception = Record.Exception(counter.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void RealCounterNeverThrows()
    {
        using var counter = IoThroughputCounter.CreateForWrites();
        if (counter == null) return;

        var exception = Record.Exception(() =>
        {
            Assert.True(counter.NextValue() >= 0);
            Assert.True(counter.NextValue() >= 0);
        });

        Assert.Null(exception);
    }
}
