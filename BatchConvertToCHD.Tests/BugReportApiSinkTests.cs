using BatchConvertToCHD.Services;
using Serilog;

namespace BatchConvertToCHD.Tests;

public class BugReportApiSinkTests
{
    private static TestBugReportService CreateTestService()
    {
        return new TestBugReportService("http://localhost", "test-key", "test-app");
    }

    [Fact]
    public void EmitDoesNotForwardDebugEvents()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Debug("Debug message");
        logger.Verbose("Verbose message");
        logger.Information("Information message");

        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public void EmitForwardsWarningEvents()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Warning("Warning message");

        Assert.Equal(1, service.CallCount);
        Assert.Contains("Warning message", service.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitForwardsErrorEvents()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Error("Error message");

        Assert.Equal(1, service.CallCount);
        Assert.Contains("Error message", service.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitForwardsFatalEvents()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Fatal("Fatal message");

        Assert.Equal(1, service.CallCount);
        Assert.Contains("Fatal message", service.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitPassesExceptionToService()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        var ex = new InvalidOperationException("Test exception");
        logger.Error(ex, "Error with exception");

        Assert.Equal(1, service.CallCount);
        Assert.NotNull(service.LastException);
        Assert.IsType<InvalidOperationException>(service.LastException);
        Assert.Equal("Test exception", service.LastException!.Message);
    }

    [Fact]
    public void EmitForwardsMultipleEventsInSequence()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        logger.Warning("First");
        logger.Error("Second");
        logger.Fatal("Third");
        logger.Information("Should be ignored");
        logger.Warning("Fourth");

        Assert.Equal(4, service.CallCount);
    }

    [Fact]
    public void EmitForwardsWarningWithException()
    {
        var service = CreateTestService();
        var sink = new BugReportApiSink(service);

        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        var ex = new ArgumentException("Arg error");
        logger.Warning(ex, "Warning with exception");

        Assert.Equal(1, service.CallCount);
        Assert.NotNull(service.LastException);
        Assert.StartsWith("Arg error", service.LastException!.Message, StringComparison.Ordinal);
    }

    private sealed class TestBugReportService(string apiUrl, string apiKey, string applicationName)
        : BugReportService(apiUrl, apiKey, applicationName)
    {
        public int CallCount { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;
        public Exception? LastException { get; private set; }

        public override Task<bool> SendBugReportAsync(
            string message,
            Exception? ex = null,
            CancellationToken token = default
        )
        {
            CallCount++;
            LastMessage = message;
            LastException = ex;
            return Task.FromResult(true);
        }
    }
}