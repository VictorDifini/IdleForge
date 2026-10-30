using System.Text.RegularExpressions;

namespace IdleForge.Core.UserScripts;

public static partial class UrlPatternMatcher
{
    public static bool AppliesTo(UserScriptMetadata metadata, Uri uri)
    {
        var url = uri.AbsoluteUri;
        if (metadata.Excludes.Any(x => Matches(x, url))) return false;
        var candidates = metadata.Matches.Concat(metadata.Includes).ToArray();
        return candidates.Length == 0 || candidates.Any(x => Matches(x, url));
    }

    public static bool Matches(string pattern, string url)
    {
        if (pattern == "<all_urls>") return Uri.TryCreate(url, UriKind.Absolute, out var all) && all.Scheme is "http" or "https";
        if (pattern.StartsWith('/') && pattern.LastIndexOf('/') > 0)
        {
            var end = pattern.LastIndexOf('/');
            try { return Regex.IsMatch(url, pattern[1..end]); } catch (ArgumentException) { return false; }
        }
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*");
        return Regex.IsMatch(url, $"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
