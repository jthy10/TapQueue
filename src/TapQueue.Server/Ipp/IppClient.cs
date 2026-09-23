using System.Net;
using System.Net.Http.Headers;

namespace TapQueue.Server.Ipp;

/// <summary>Sends IPP requests to physical printers.</summary>
public sealed class IppClient : IDisposable
{
    private static int _nextRequestId;
    private readonly HttpClient _http;

    public IppClient(bool tlsSkipVerify, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        if (tlsSkipVerify)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler) { Timeout = timeout };
    }

    public static int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>ipp://host/path → http://host:631/path, ipps:// → https://host:631/path.</summary>
    public static Uri ToHttpUri(string printerUri)
    {
        var uri = new Uri(printerUri);
        var builder = new UriBuilder(uri);
        if (uri.Scheme is "ipp" or "ipps")
        {
            builder.Scheme = uri.Scheme == "ipp" ? "http" : "https";
            builder.Port = uri.IsDefaultPort || uri.Port <= 0 ? 631 : uri.Port;
        }
        return builder.Uri;
    }

    public async Task<IppMessage> SendAsync(string printerUri, IppMessage request, Stream? document = null, CancellationToken ct = default)
    {
        using var content = new IppRequestContent(IppWriter.Encode(request), document);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ToHttpUri(printerUri)) { Content = content };
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Printer returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await IppReader.ReadAsync(body, ct);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The encoded IPP header followed by the document, streamed with a known length.</summary>
    private sealed class IppRequestContent : HttpContent
    {
        private readonly byte[] _header;
        private readonly Stream? _document;

        public IppRequestContent(byte[] header, Stream? document)
        {
            _header = header;
            _document = document;
            Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_header);
            if (_document is not null)
                await _document.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_document is null)
            {
                length = _header.Length;
                return true;
            }
            if (_document.CanSeek)
            {
                length = _header.Length + _document.Length - _document.Position;
                return true;
            }
            length = 0;
            return false;
        }
    }
}
