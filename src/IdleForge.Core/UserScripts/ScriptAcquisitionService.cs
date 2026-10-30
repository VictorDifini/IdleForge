using IdleForge.Core.Networking;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IdleForge.Core.UserScripts;

public sealed record AcquiredScript(Uri SourceUri, string Source, UserScriptMetadata Metadata);

public sealed partial class ScriptAcquisitionService
{
    private readonly HttpClient _client;
    public ScriptAcquisitionService(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null };
        _client = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<AcquiredScript> AcquireAsync(Uri input, CancellationToken cancellationToken = default)
    {
        if (input.Scheme != Uri.UriSchemeHttps) throw new UnauthorizedAccessException("A instalação remota exige HTTPS.");
        var uri = input;
        for (var redirects = 0; ; redirects++)
        {
            await ValidatePublicAsync(uri, cancellationToken);
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is not null)
            {
                if (redirects >= 9) throw new HttpRequestException("Limite de redirecionamentos excedido.");
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                if (uri.Scheme != Uri.UriSchemeHttps) throw new UnauthorizedAccessException("Redirecionamento para protocolo não seguro bloqueado.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            var source = await response.Content.ReadAsStringAsync(cancellationToken);
            if (source.Length > 4 * 1024 * 1024) throw new InvalidDataException("O script excede 4 MiB.");
            if (!source.Contains("==UserScript==", StringComparison.Ordinal))
            {
                var match = UserScriptLink().Match(source);
                if (!match.Success) throw new FormatException("A página não contém um link reconhecível para um userscript.");
                return await AcquireAsync(new Uri(uri, System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)), cancellationToken);
            }
            return new(uri, source, new UserScriptParser().Parse(source));
        }
    }

    internal static async Task ValidatePublicAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new UnauthorizedAccessException("URL remota insegura.");
        var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(ConnectPolicy.IsRestricted)) throw new UnauthorizedAccessException("Destino local, privado ou reservado bloqueado.");
    }

    [GeneratedRegex("href=[\\\"']([^\\\"']+\\.user\\.js(?:\\?[^\\\"']*)?)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex UserScriptLink();
}

public sealed class RequireCache
{
    private readonly string _root;
    private readonly HttpMessageHandler? _handler;
    public RequireCache(string root, HttpMessageHandler? handler = null) { _root = root; _handler = handler; }
    public async Task<IReadOnlyList<string>> ResolveAsync(IEnumerable<Uri> requirements, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root); var result = new List<string>();
        foreach (var uri in requirements)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new UnauthorizedAccessException("@require remoto exige HTTPS.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var path = Path.Combine(_root, key + ".js");
            if (!File.Exists(path))
            {
                var fetchUri = new UriBuilder(uri) { Fragment = "" }.Uri;
                using var broker = new SecureHttpBroker(_handler);
                var response = await broker.SendAsync(new BrokerRequest(Guid.NewGuid().ToString("N"), "GET", fetchUri), new ConnectPolicy([fetchUri.Host]), cancellationToken);
                if (response.Status is < 200 or >= 300) throw new HttpRequestException($"@require retornou HTTP {response.Status}.");
                var text = response.ResponseText ?? ""; var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Dependência @require excede 4 MiB.");
                ValidateIntegrity(uri.Fragment, bytes);
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(temporary, text, cancellationToken); File.Move(temporary, path, true);
            }
            result.Add(await File.ReadAllTextAsync(path, cancellationToken));
        }
        return result;
    }

    private static void ValidateIntegrity(string fragment, byte[] bytes)
    {
        if (string.IsNullOrEmpty(fragment)) return;
        var token = fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase));
        if (token is null) return;
        var expected = token[7..]; var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A integridade SHA-256 de @require não confere.");
    }
}
