using System.Text.RegularExpressions;

namespace IdleForge.Core.UserScripts;

public sealed partial class UserScriptParser
{
    public UserScriptMetadata Parse(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var block = MetadataBlock().Match(source);
        if (!block.Success) throw new FormatException("Bloco // ==UserScript== não encontrado.");

        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match line in MetadataLine().Matches(block.Groups[1].Value))
        {
            var key = line.Groups[1].Value;
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(line.Groups[2].Value.Trim());
        }

        string? One(string key) => values.GetValueOrDefault(key)?.LastOrDefault();
        IReadOnlyList<string> Many(string key) => values.GetValueOrDefault(key) ?? [];
        var resources = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var resource in Many("resource"))
        {
            var parts = resource.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) resources[parts[0]] = uri;
        }

        return new UserScriptMetadata
        {
            Name = One("name") ?? "Script sem nome",
            Namespace = One("namespace"), Version = One("version") ?? "0.0.0",
            Description = One("description"), Author = One("author"),
            Matches = Many("match"), Includes = Many("include"), Excludes = Many("exclude"),
            Grants = Many("grant"), Connect = Many("connect"), NoFrames = values.ContainsKey("noframes"),
            Requires = Many("require").Select(TryAbsoluteUri).Where(x => x is not null).Cast<Uri>().ToArray(),
            Resources = resources, UpdateUrl = TryAbsoluteUri(One("updateURL")),
            DownloadUrl = TryAbsoluteUri(One("downloadURL")), RunAt = ParseRunAt(One("run-at")),
            Sandbox = One("sandbox")
        };
    }

    private static Uri? TryAbsoluteUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    private static ScriptRunAt ParseRunAt(string? value) => value?.ToLowerInvariant() switch
    {
        "document-start" => ScriptRunAt.DocumentStart, "document-body" => ScriptRunAt.DocumentBody,
        "document-end" => ScriptRunAt.DocumentEnd, _ => ScriptRunAt.DocumentIdle
    };

    [GeneratedRegex(@"(?ms)^\s*//\s*==UserScript==\s*$([\s\S]*?)^\s*//\s*==/UserScript==\s*$")]
    private static partial Regex MetadataBlock();
    [GeneratedRegex(@"(?m)^\s*//\s*@([\w-]+)(?:\s+(.*?))?\s*$")]
    private static partial Regex MetadataLine();
}
