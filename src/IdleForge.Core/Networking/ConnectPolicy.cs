using System.Net;
using System.Net.Sockets;
using System.Globalization;

namespace IdleForge.Core.Networking;

public sealed class ConnectPolicy(IReadOnlyList<string> declarations, bool broadAccessApproved = false)
{
    public async Task ValidateAsync(Uri target, CancellationToken cancellationToken = default)
    {
        if (!target.IsAbsoluteUri || target.Scheme is not ("http" or "https"))
            throw new UnauthorizedAccessException("Somente destinos HTTP e HTTPS são permitidos.");
        if (!string.IsNullOrEmpty(target.UserInfo))
            throw new UnauthorizedAccessException("Credenciais embutidas na URL não são permitidas.");
        if (!IsDeclared(target.Host))
            throw new UnauthorizedAccessException($"O domínio {target.IdnHost} não foi autorizado por @connect.");

        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(target.DnsSafeHost, cancellationToken); }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        { throw new UnauthorizedAccessException("Não foi possível validar o endereço do destino.", ex); }
        if (addresses.Length == 0 || addresses.Any(IsRestricted))
            throw new UnauthorizedAccessException("Destinos locais, privados, reservados ou de metadados são bloqueados.");
    }

    private bool IsDeclared(string host)
    {
        var normalized = Normalize(host);
        foreach (var raw in declarations)
        {
            var rule = raw.Trim();
            if (rule == "*" && broadAccessApproved) return true;
            if (rule == "*") continue;
            if (Uri.TryCreate(rule, UriKind.Absolute, out var uri)) rule = uri.Host;
            rule = Normalize(rule.TrimStart('.'));
            if (rule.StartsWith("*.", StringComparison.Ordinal)) rule = rule[2..];
            if (normalized == rule || normalized.EndsWith('.' + rule, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Normalize(string host) => new IdnMapping().GetAscii(host).TrimEnd('.').ToLowerInvariant();

    public static bool IsRestricted(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 || bytes[0] >= 224 || address.Equals(IPAddress.Parse("100.100.100.200"));
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
               address.Equals(IPAddress.IPv6Loopback) || (bytes[0] & 0xFE) == 0xFC;
    }
}
