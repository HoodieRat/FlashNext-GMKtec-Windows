using System.Net;
using System.Text;

namespace FlashNext.IntegrationTests;

public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return responder(request);
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string content, string mediaType = "application/json") => new(status) { Content = new StringContent(content, Encoding.UTF8, mediaType) };
}
