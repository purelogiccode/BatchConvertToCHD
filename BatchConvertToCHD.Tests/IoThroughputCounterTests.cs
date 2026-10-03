using System.Diagnostics;
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
        using var counter = new IoThroughputCounter(static () => throw new InvalidOperationException("boom")
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

    [Fact]
    public async Task CreateForProcessObservesChildWrites()
    {
        // chdman runs out of process; the speed display samples the child's counters, so this is
        // the property that makes the card show a non-zero speed during a chdman conversion.
        if (!PlatformSupported) return;

        var source = Path.Combine(Path.GetTempPath(), $"io_counter_src_{Guid.NewGuid():N}.bin");
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"io_counter_dst_{Guid.NewGuid():N}.bin"
        );
        File.WriteAllBytes(source, new byte[32 * 1024 * 1024]);

        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(
                    "cmd.exe",
                    $"/c copy /b \"{source}\" \"{destination}\" > nul"
                )
                : new ProcessStartInfo("/bin/cp", $"\"{source}\" \"{destination}\"");
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;

            using var process = Process.Start(startInfo)!;
            using var counter = IoThroughputCounter.CreateForProcess(process, writes: true);
            if (counter == null) return;

            counter.NextValue(); // Baseline before the child writes anything.
            await process.WaitForExitAsync();
            await Task.Delay(100);

            Assert.True(
                counter.NextValue() > 0,
                "the child process's writes should be visible to its counter"
            );
        }
        finally
        {
            TryDelete(source);
            TryDelete(destination);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}