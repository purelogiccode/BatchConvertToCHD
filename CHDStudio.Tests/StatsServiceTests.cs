using System.Net;
using System.Text.Json;
using CHDStudio.Services;

namespace CHDStudio.Tests;

public class StatsServiceTests
{
    private const string TestApiUrl = "https://example.com/api/stats";
    private const string TestApiKey = "test-api-key";
    private const string TestAppId = "test-app-id";

    [Fact]
    public void ConstructorStoresParametersCorrectly()
    {
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId);

        Assert.Equal(TestApiUrl, service.ApiUrl);
        Assert.Equal(TestApiKey, service.ApiKey);
        Assert.Equal(TestAppId, service.ApplicationId);
    }

    [Fact]
    public async Task InternalConstructorUsesInjectedHttpClient()
    {
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);

        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);
        await service.RecordUsageAsync();

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task RecordUsageAsyncSendsCorrectHttpMethod()
    {
        HttpMethod? capturedMethod = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedMethod = req.Method;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.Equal(HttpMethod.Post, capturedMethod);
    }

    [Fact]
    public async Task RecordUsageAsyncSendsCorrectUrl()
    {
        string? capturedUrl = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.Equal(TestApiUrl, capturedUrl);
    }

    [Fact]
    public async Task RecordUsageAsyncIncludesAuthorizationHeader()
    {
        string? capturedAuth = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.NotNull(capturedAuth);
        Assert.Equal("Bearer", capturedAuth!.Split(' ')[0]);
        Assert.Equal(TestApiKey, capturedAuth.Split(' ')[1]);
    }

    [Fact]
    public async Task RecordUsageAsyncSendsApplicationIdInBody()
    {
        string? capturedBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("applicationId", out var appIdElement));
        Assert.Equal(TestAppId.ToLowerInvariant(), appIdElement.GetString());
    }

    [Fact]
    public async Task RecordUsageAsyncSendsVersionInBody()
    {
        string? capturedBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("version", out var versionElement));
        Assert.NotEmpty(versionElement.GetString()!);
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnNetworkError()
    {
        var service = new StatsService(
            "https://invalid.example.invalid/api",
            TestApiKey,
            TestAppId
        );
        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnRateLimitResponse()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.TooManyRequests,
            "{\"error\":\"Rate limit exceeded\"}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnUnauthorizedResponse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{\"error\":\"Unauthorized\"}");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnBadRequestResponse()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":\"applicationId is required\"}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnServerError()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            "{\"error\":\"Server error\"}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnSuccess()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"message\":\"Stats recorded successfully\",\"applicationId\":\"test-app-id\"}"
        );
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordUsageAsyncOnSuccessSendsExactlyOneRequest()
    {
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task RecordUsageAsyncOnRateLimitSendsExactlyOneRequest()
    {
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"Rate limit exceeded\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task RecordUsageAsyncPayloadHasExactlyTwoProperties()
    {
        string? capturedBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task RecordUsageAsyncVersionMatchesApplicationAssemblyVersion()
    {
        string? capturedBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":\"ok\"}")
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId, httpClient);

        await service.RecordUsageAsync();

        var expected = typeof(StatsService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(expected, doc.RootElement.GetProperty("version").GetString());
    }
}