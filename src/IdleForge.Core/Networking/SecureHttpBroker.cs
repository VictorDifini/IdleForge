using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace IdleForge.Core.Networking;

public sealed record BrokerRequest(string Id, string Method, Uri Url, IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null, int TimeoutMilliseconds = 30_000, string ResponseType = "text");
public sealed record BrokerResponse(int Status, string StatusText, Uri FinalUrl, IReadOnlyDictionary<string, string[]> Headers,
    string? ResponseText, object? Response, byte[]? ResponseBytes = null);

public sealed class SecureHttpBroker : IDisposable
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Cookie", "Cookie2", "Authorization", "Proxy-Authorization", "Host", "Origin", "Referer", "Connection", "Transfer-Encoding", "Content-Length" };
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);

    public SecureHttpBroker(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<BrokerResponse> SendAsync(BrokerRequest request, ConnectPolicy policy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128) throw new ArgumentException("Identificador inválido.");
        var timeout = Math.Clamp(request.TimeoutMilliseconds, 1, 300_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        if (!_requests.TryAdd(request.Id, linked)) throw new InvalidOperationException("Uma requisição com esse identificador já está ativa.");
        try
        {
            var uri = request.Url;
            var method = new HttpMethod(string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.ToUpperInvariant());
            for (var redirects = 0; ; redirects++)
            {
                await policy.ValidateAsync(uri, linked.Token);
                using var message = new HttpRequestMessage(method, uri);
                if (request.Body is not null) message.Content = new StringContent(request.Body, Encoding.UTF8);
                AddHeaders(message, request.Headers);
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
                {
                    if (redirects >= 9) throw new HttpRequestException("Limite de redirecionamentos excedido.");
                    uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    if (response.StatusCode is HttpStatusCode.SeeOther or HttpStatusCode.Moved or HttpStatusCode.Found) method = HttpMethod.Get;
                    continue;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token);
                if (bytes.Length > 16 * 1024 * 1024) throw new HttpRequestException("Resposta excede o limite de 16 MiB.");
                var text = Encoding.UTF8.GetString(bytes);
                object? value = request.ResponseType.Equals("json", StringComparison.OrdinalIgnoreCase)
                    ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(text) : text;
                var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                return new((int)response.StatusCode, response.ReasonPhrase ?? "", uri, headers, text, value, bytes);
            }
        }
        finally { _requests.TryRemove(request.Id, out _); }
    }

    public bool Abort(string requestId) { if (!_requests.TryGetValue(requestId, out var source)) return false; source.Cancel(); return true; }

    private static void AddHeaders(HttpRequestMessage message, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (name, value) in headers)
        {
            if (ForbiddenHeaders.Contains(name) || name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new UnauthorizedAccessException($"O header {name} não é permitido.");
            if (!message.Headers.TryAddWithoutValidation(name, value) && !(message.Content?.Headers.TryAddWithoutValidation(name, value) ?? false))
                throw new UnauthorizedAccessException($"O header {name} não é aceito.");
        }
    }
    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;
    public void Dispose() { foreach (var request in _requests.Values) request.Cancel(); _client.Dispose(); }
}
