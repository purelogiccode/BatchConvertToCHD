using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class AppHttpClientTests
{
    [Fact]
    public void ClientReturnsNonNullHttpClient()
    {
        var client = AppHttpClient.Client;
        Assert.NotNull(client);
    }

    [Fact]
    public void ClientReturnsSameInstance()
    {
        var client1 = AppHttpClient.Client;
        var client2 = AppHttpClient.Client;
        Assert.Same(client1, client2);
    }

    [Fact]
    public void ClientHasAcceptJsonHeader()
    {
        var client = AppHttpClient.Client;
        Assert.True(client.DefaultRequestHeaders.Accept.Count > 0);
        Assert.Contains(
            client.DefaultRequestHeaders.Accept,
            static m => string.Equals(m.MediaType, "application/json", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ClientUsesTls12And13()
    {
        var handlerField = typeof(AppHttpClient).GetField(
            "_handler",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(handlerField);
        var handler = handlerField.GetValue(null);
        Assert.NotNull(handler);

        var sslOptionsProp = handler
            .GetType()
            .GetProperty("SslOptions", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(sslOptionsProp);
        var sslOptions = sslOptionsProp.GetValue(handler) as SslClientAuthenticationOptions;
        Assert.NotNull(sslOptions);

        Assert.True(
            sslOptions.EnabledSslProtocols.HasFlag(SslProtocols.Tls12),
            "TLS 1.2 should be enabled"
        );
        Assert.True(
            sslOptions.EnabledSslProtocols.HasFlag(SslProtocols.Tls13),
            "TLS 1.3 should be enabled"
        );
    }

    [Fact]
    public void DisposeClearsClientAndHandler()
    {
        var clientBefore = AppHttpClient.Client;
        Assert.NotNull(clientBefore);

        AppHttpClient.Dispose();

        var clientField = typeof(AppHttpClient).GetField(
            "_client",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(clientField);
        Assert.Null(clientField.GetValue(null));

        var handlerField = typeof(AppHttpClient).GetField(
            "_handler",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(handlerField);
        Assert.Null(handlerField.GetValue(null));
    }

    [Fact]
    public void ClientAfterDisposeReturnsNewInstance()
    {
        var client1 = AppHttpClient.Client;
        AppHttpClient.Dispose();
        var client2 = AppHttpClient.Client;

        Assert.NotSame(client1, client2);
        Assert.NotNull(client2);
    }

    [Fact]
    public void DisposeCanBeCalledMultipleTimes()
    {
        AppHttpClient.Dispose();
        var exception = Record.Exception(AppHttpClient.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public async Task ClientIsThreadSafe()
    {
        var clients = new HttpClient[10];
        var tasks = new Task[10];

        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() => { clients[index] = AppHttpClient.Client; });
        }

        await Task.WhenAll(tasks);

        var first = clients[0];
        for (var i = 1; i < 10; i++)
        {
            Assert.Same(first, clients[i]);
        }
    }
}