namespace IdleForge.Core.UserScripts;

public sealed class ScriptRiskAnalyzer
{
    private static readonly HashSet<string> HighRiskGrants = new(StringComparer.OrdinalIgnoreCase)
    { "GM_xmlhttpRequest", "GM.xmlHttpRequest", "unsafeWindow", "GM_download", "GM.download" };

    public ScriptRisk Analyze(UserScriptMetadata metadata)
    {
        var findings = new List<string>();
        var score = 0;
        if (metadata.Matches.Contains("<all_urls>")) { score += 3; findings.Add("Executa em todos os sites HTTP/HTTPS."); }
        var elevated = metadata.Grants.Where(HighRiskGrants.Contains).ToArray();
        if (elevated.Length > 0) { score += 2; findings.Add($"APIs sensíveis: {string.Join(", ", elevated)}."); }
        if (metadata.Connect.Count > 0) { score += 2; findings.Add($"Acesso externo: {string.Join(", ", metadata.Connect)}."); }
        if (metadata.Requires.Count > 0) { score += 1; findings.Add($"Carrega {metadata.Requires.Count} dependência(s) remota(s)."); }
        if (metadata.UpdateUrl is not null || metadata.DownloadUrl is not null) { score += 1; findings.Add("Pode obter atualizações fora da loja de origem."); }
        var level = score switch { >= 6 => RiskLevel.Critical, >= 4 => RiskLevel.High, >= 2 => RiskLevel.Medium, _ => RiskLevel.Low };
        if (findings.Count == 0) findings.Add("Nenhuma permissão sensível declarada.");
        return new ScriptRisk(level, findings);
    }
}
