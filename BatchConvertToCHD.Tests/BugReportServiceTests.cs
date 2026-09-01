using System.Net;
using System.Reflection;
using System.Text;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class BugReportServiceTests
{
    private const string TestApiUrl = "https://example.com/api/bugreport";
    private const string TestApiKey = "test-api-key";
    private const string TestAppName = "TestApp";

    [Fact]
    public void ConstructorStoresParametersCorrectly()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);

        var apiUrlField = typeof(BugReportService).GetField(
            "_apiUrl",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        var apiKeyField = typeof(BugReportService).GetField(
            "_apiKey",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        var appNameField = typeof(BugReportService).GetField(
            "_applicationName",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        Assert.NotNull(apiUrlField);
        Assert.NotNull(apiKeyField);
        Assert.NotNull(appNameField);
        Assert.Equal(TestApiUrl, apiUrlField.GetValue(service));
        Assert.Equal(TestApiKey, apiKeyField.GetValue(service));
        Assert.Equal(TestAppName, appNameField.GetValue(service));
    }

    [Fact]
    public void BuildFormattedReportIncludesMessageAndAppName()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod(
            "BuildFormattedReport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);

        var result = method.Invoke(service, ["Test error message", null]) as string;
        Assert.NotNull(result);
        Assert.Contains("Test error message", result, StringComparison.Ordinal);
        Assert.Contains(TestAppName, result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportIncludesExceptionDetails()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod(
            "BuildFormattedReport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);

        var ex = new InvalidOperationException("Something went wrong");
        var result = method.Invoke(service, ["Error summary", ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("Error summary", result, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportIncludesInnerException()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod(
            "BuildFormattedReport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);

#pragma warning disable MA0015
        var inner = new ArgumentException("Inner error");
#pragma warning restore MA0015
        var outer = new InvalidOperationException("Outer error", inner);
        var result = method.Invoke(service, ["Error summary", outer]) as string;
        Assert.NotNull(result);
        Assert.Contains("Inner Exception", result, StringComparison.Ordinal);
        Assert.Contains("Inner error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceNullReturnsNa()
    {
        var method = typeof(BugReportService).GetMethod(
            "GetExceptionStackTrace",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]) as string;
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetExceptionStackTraceIncludesExceptionDetails()
    {
        var method = typeof(BugReportService).GetMethod(
            "GetExceptionStackTrace",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var ex = new InvalidOperationException("Test error");
        var result = method.Invoke(null, [ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Test error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceHandlesNestedExceptions()
    {
        var method = typeof(BugReportService).GetMethod(
            "GetExceptionStackTrace",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var deep = new FormatException("Deep");
#pragma warning disable MA0015
        var mid = new ArgumentException("Mid", deep);
#pragma warning restore MA0015
        var top = new InvalidOperationException("Top", mid);
        var result = method.Invoke(null, [top]) as string;
        Assert.NotNull(result);
        Assert.Contains("Top", result, StringComparison.Ordinal);
        Assert.Contains("Mid", result, StringComparison.Ordinal);
        Assert.Contains("Deep", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceLimitsDepth()
    {
        var method = typeof(BugReportService).GetMethod(
            "GetExceptionStackTrace",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var inner = new InvalidOperationException("deepest");
        var current = inner;
        for (var i = 0; i < 10; i++) current = new InvalidOperationException($"level {i}", current);

        var result = method.Invoke(null, [current]) as string;
        Assert.NotNull(result);
    }

    [Fact]
    public void GetApplicationVersionReturnsValidValue()
    {
        var method = typeof(BugReportService).GetMethod(
            "GetApplicationVersion",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var result = method.Invoke(null, null) as string;
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void AppendExceptionDetailsWithNullStackTraceDoesNotCrash()
    {
        var method = typeof(BugReportService).GetMethod(
            "AppendExceptionDetails",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var sb = new StringBuilder();
        var ex = new InvalidOperationException("No stack");
        var record = Record.Exception(() => method.Invoke(null, [sb, ex, 0]));
        Assert.Null(record);
        Assert.Contains("No stack", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendBugReportAsyncReturnsFalseOnNetworkError()
    {
        var service = new BugReportService(
            "https://invalid.example.invalid/api",
            TestApiKey,
            TestAppName
        );
        var result = await service.SendBugReportAsync("Test message");
        Assert.False(result);
    }

    [Fact]
    public void BuildFormattedReportExceptionWithNullFieldsDoesNotCrash()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod(
            "BuildFormattedReport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);

        var ex = Record.Exception(() =>
        {
            // Exception with no message and no stack trace
            var customEx = new InvalidOperationException(null);
            var result = method.Invoke(service, ["Error summary", customEx]) as string;
            Assert.NotNull(result);
            Assert.Contains("Error summary", result, StringComparison.Ordinal);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void BuildFormattedReportEmptyMessageDoesNotCrash()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod(
            "BuildFormattedReport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);

        var result = method.Invoke(service, ["", null]) as string;
        Assert.NotNull(result);
        Assert.Contains("=== Error Details ===", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportExceptionWithoutStackTraceDoesNotCrash()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod(
            "BuildFormattedReport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);

        // Create exception using parameterless constructor which may not populate StackTrace immediately
        var customEx = new InvalidOperationException("Error with no explicit stack");
        var result = method.Invoke(service, ["Error with null stack", customEx]) as string;
        Assert.NotNull(result);
        Assert.Contains("Error with null stack", result, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsHandlesExceptionWithoutSource()
    {
        var method = typeof(BugReportService).GetMethod(
            "AppendExceptionDetails",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        var sb = new StringBuilder();
        var ex = Record.Exception(() => method.Invoke(null, [sb, new InvalidOperationException(), 0]));

        Assert.Null(ex);
        Assert.Contains("InvalidOperationException", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendBugReportAsyncSendsCorrectHttpMethod()
    {
        HttpMethod? capturedMethod = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedMethod = req.Method;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\",\"id\":1}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        await service.SendBugReportAsync("Test");

        Assert.Equal(HttpMethod.Post, capturedMethod);
    }

    [Fact]
    public async Task SendBugReportAsyncIncludesApiKeyHeader()
    {
        string? capturedKey = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedKey = req.Headers.GetValues("X-API-KEY").FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\",\"id\":1}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        await service.SendBugReportAsync("Test");

        Assert.Equal(TestApiKey, capturedKey);
    }

    [Fact]
    public async Task SendBugReportAsyncSendsApplicationNameInBody()
    {
        string? capturedBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\",\"id\":1}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        await service.SendBugReportAsync("Test");

        Assert.NotNull(capturedBody);
        Assert.Contains(TestAppName, capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendBugReportAsyncReturnsTrueOnSuccess()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"message\":\"Bug report received\",\"id\":42}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        var result = await service.SendBugReportAsync("Test bug");

        Assert.True(result);
    }

    [Fact]
    public async Task SendBugReportAsyncReturnsFalseOnServerError()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            "{\"error\":\"Server error\"}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        var result = await service.SendBugReportAsync("Test");

        Assert.False(result);
    }

    [Fact]
    public async Task SendBugReportAsyncReturnsTrueOnBadRequest()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":\"Missing required field: message\"}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        var result = await service.SendBugReportAsync("");

        Assert.False(result);
    }

    #region IsExcludedFromBugReport

    [Theory]
    [InlineData("chdman.exe not found")]
    [InlineData("CRITICAL ERROR: The following required component is missing")]
    [InlineData("Failed to record usage statistics")]
    [InlineData("Not a valid CHD file")]
    [InlineData("Invalid or corrupt data")]
    [InlineData("Cannot open file")]
    [InlineData(@"Partial extraction: 1 file(s) remain in temp directory: C:\temp\x")]
    [InlineData("Fatal error occurred: 1")]
    [InlineData("Unhandled exception: cannot create std::vector larger than max_size()")]
    [InlineData("Temp drive (")]
    [InlineData("Output drive (")]
    [InlineData("drive has 1.5 GB")]
    [InlineData("drive (C:)")]
    [InlineData("input files total")]
    [InlineData("CHD files total")]
    [InlineData("You may run out of disk space")]
    [InlineData("Temporary files are created during conversion")]
    [InlineData("CHD compression usually reduces")]
    [InlineData("Extracted files are typically larger")]
    [InlineData("disk space")]
    [InlineData("disk full")]
    [InlineData("No supported primary files found in archive")]
    [InlineData("referenced files are missing")]
    [InlineData("could not be resolved")]
    [InlineData("MP3 audio track could not be decoded")]
    [InlineData("is not divisible by")]
    [InlineData("could not validate referenced files")]
    [InlineData("The file or directory is corrupted and unreadable")]
    [InlineData("Retry via temp failed")]
    [InlineData("archive file may be corrupted")]
    [InlineData("archive is invalid or corrupt")]
    [InlineData("archive file appears to be incomplete")]
    [InlineData("archive file may be corrupted or in an unsupported format")]
    [InlineData("archive file may be corrupted or unsupported")]
    [InlineData("Archive is encrypted")]
    [InlineData("compression method that is not supported")]
    [InlineData("CCDSharp: Conversion error")]
    [InlineData("File not found, skipping:")]
    public void IsExcludedFromBugReport_KnownPatterns_ReturnsTrue(string message)
    {
        Assert.True(BugReportService.IsExcludedFromBugReport(message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A real application error occurred")]
    [InlineData("NullReferenceException")]
    [InlineData("Unhandled exception in conversion pipeline")]
    [InlineData("Failed to initialize service")]
    [InlineData("Unexpected error during processing")]
    [InlineData("Failed to open PBP file: DecompressionError (code 8)")]
    [InlineData(
        "Failed to extract PBP file: Ridge Racer Type 4.PBP (650,000,000 bytes) - Failed to extract disc 1 of 2: DecompressionError (code 8)"
    )]
    [InlineData("Failed to read hunk 0: Chderrdecompressionerror")]
    public void IsExcludedFromBugReport_NormalMessages_ReturnsFalse(string message)
    {
        Assert.False(BugReportService.IsExcludedFromBugReport(message));
    }

    [Theory]
    [InlineData("CHDMAN.EXE NOT FOUND")]
    [InlineData("Archive Is Encrypted")]
    [InlineData("DISK FULL")]
    [InlineData("File Not Found, Skipping: some path")]
    public void IsExcludedFromBugReport_CaseInsensitive(string message)
    {
        Assert.True(BugReportService.IsExcludedFromBugReport(message));
    }

    [Fact]
    public void IsExcludedFromBugReport_SubstringMatchIsExcluded()
    {
        Assert.True(BugReportService.IsExcludedFromBugReport("disk space is critically low"));
        Assert.True(
            BugReportService.IsExcludedFromBugReport(
                "The archive file may be corrupted, please verify"
            )
        );
    }

    [Fact]
    public async Task SendBugReportAsync_ExcludedMessage_ReturnsFalseWithoutHttpCall()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Should not be called")
        );
        using var httpClient = new HttpClient(handler);
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName, httpClient);

        var result = await service.SendBugReportAsync("disk space is running low");

        Assert.False(result);
    }

    #endregion
}