using System.Net;

namespace BatchConvertToCHD.Tests;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _handler;
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _asyncHandler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    public FakeHttpMessageHandler(
        HttpStatusCode statusCode,
        string content,
        string contentType = "application/json"
    )
        : this(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, contentType),
        })
    {
    }

    public static FakeHttpMessageHandler WithAsyncHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncHandler
    )
    {
        return new FakeHttpMessageHandler(asyncHandler);
    }

    private FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncHandler)
    {
        _asyncHandler = asyncHandler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (_asyncHandler != null)
            return _asyncHandler(request);

        return Task.FromResult(_handler!(request));
    }
}