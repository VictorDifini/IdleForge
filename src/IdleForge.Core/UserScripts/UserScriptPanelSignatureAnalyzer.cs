using System.Text.RegularExpressions;

namespace IdleForge.Core.UserScripts;

/// <summary>Extracts high-confidence element IDs whose ownership is evidenced by creation and insertion in the userscript source.</summary>
public sealed partial class UserScriptPanelSignatureAnalyzer
{
    public IReadOnlyList<string> FindOwnedElementIds(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var constants = StringConstant().Matches(source).Cast<Match>()
            .GroupBy(x => x.Groups[1].Value, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last().Groups[3].Value, StringComparer.Ordinal);
        var created = CreatedElement().Matches(source).Cast<Match>().Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in InsertedElement().Matches(source)) inserted.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in IdAssignment().Matches(source))
        {
            var variable = match.Groups[1].Value;
            if (!created.Contains(variable) || !inserted.Contains(variable)) continue;
            var id = Resolve(match.Groups[3].Value, match.Groups[4].Value, constants);
            if (IsSafeId(id)) result.Add(id!);
        }
        foreach (Match match in SetIdAttribute().Matches(source))
        {
            var variable = match.Groups[1].Value;
            if (!created.Contains(variable) || !inserted.Contains(variable)) continue;
            var id = Resolve(match.Groups[4].Value, match.Groups[5].Value, constants);
            if (IsSafeId(id)) result.Add(id!);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string? Resolve(string literal, string identifier, IReadOnlyDictionary<string, string> constants) =>
        !string.IsNullOrEmpty(literal) ? literal : constants.GetValueOrDefault(identifier);
    private static bool IsSafeId(string? value) => value is { Length: >= 3 and <= 160 } && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_.:-]*$");

    [GeneratedRegex("""\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(['"])([^'"\r\n]{1,160})\2""")]
    private static partial Regex StringConstant();
    [GeneratedRegex(@"\b(?:const\s+|let\s+|var\s+)?([A-Za-z_$][\w$]*)\s*=\s*document\.createElement(?:NS)?\s*\(")]
    private static partial Regex CreatedElement();
    [GeneratedRegex(@"(?:\.appendChild\s*\(\s*([A-Za-z_$][\w$]*)\s*\)|\.append\s*\([^;\n]*?\b([A-Za-z_$][\w$]*)\b[^;\n]*?\))")]
    private static partial Regex InsertedElement();
    [GeneratedRegex("""\b([A-Za-z_$][\w$]*)\.id\s*=\s*(?:(['"])([^'"\r\n]{1,160})\2|([A-Za-z_$][\w$]*))""")]
    private static partial Regex IdAssignment();
    [GeneratedRegex("""\b([A-Za-z_$][\w$]*)\.setAttribute\s*\(\s*(['"])id\2\s*,\s*(?:(['"])([^'"\r\n]{1,160})\3|([A-Za-z_$][\w$]*))""")]
    private static partial Regex SetIdAttribute();
}
