using IdleForge.Core;
using IdleForge.Core.UserScripts;

namespace IdleForge.Core.Tests;

public sealed class UrlPatternMatcherTests
{
    [Theory]
    [InlineData("https://*.example.com/*", "https://game.example.com/play", true)]
    [InlineData("https://game.example/*", "http://game.example/play", false)]
    [InlineData("<all_urls>", "https://anywhere.test/x", true)]
    public void Matches_common_patterns(string pattern, string url, bool expected) => Assert.Equal(expected, UrlPatternMatcher.Matches(pattern, url));

    [Fact]
    public void Exclude_wins_over_match()
    {
        var metadata = new UserScriptMetadata { Matches = ["https://game.example/*"], Excludes = ["https://game.example/logout*"] };
        Assert.True(UrlPatternMatcher.AppliesTo(metadata, new("https://game.example/play")));
        Assert.False(UrlPatternMatcher.AppliesTo(metadata, new("https://game.example/logout-now")));
    }

    [Theory]
    [InlineData("<all_urls>", "file:///c:/secret.txt", false)]
    [InlineData("/[invalid/", "https://example.com", false)]
    [InlineData("https://example.com/*", "https://evil.example.net/https://example.com/", false)]
    public void Rejects_invalid_or_out_of_scope_matches(string pattern, string url, bool expected) => Assert.Equal(expected, UrlPatternMatcher.Matches(pattern, url));
}
