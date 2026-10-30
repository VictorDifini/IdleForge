using IdleForge.Core;
using IdleForge.Core.UserScripts;

namespace IdleForge.Core.Tests;

public sealed class UserScriptParserTests
{
    private const string Source = """
        // ==UserScript==
        // @name        Forge Helper
        // @namespace   local.tests
        // @version     1.2.3
        // @match       https://game.example/*
        // @exclude     https://game.example/logout*
        // @grant       GM_getValue
        // @grant       GM_xmlhttpRequest
        // @connect     api.example
        // @require     https://cdn.example/lib.js
        // @resource    icon https://cdn.example/icon.png
        // @run-at      document-start
        // @sandbox     raw
        // @noframes
        // ==/UserScript==
        console.log('ready');
        """;

    [Fact]
    public void Parses_repeated_and_structured_metadata()
    {
        var metadata = new UserScriptParser().Parse(Source);
        Assert.Equal("Forge Helper", metadata.Name); Assert.Equal("1.2.3", metadata.Version);
        Assert.Equal(ScriptRunAt.DocumentStart, metadata.RunAt); Assert.True(metadata.NoFrames);
        Assert.Equal("raw", metadata.Sandbox);
        Assert.Contains("GM_xmlhttpRequest", metadata.Grants); Assert.Single(metadata.Requires);
        Assert.Equal("https://cdn.example/icon.png", metadata.Resources["icon"].AbsoluteUri);
    }

    [Fact]
    public void Rejects_source_without_metadata_block() => Assert.Throws<FormatException>(() => new UserScriptParser().Parse("alert(1)"));

    [Fact]
    public void Risk_analyzer_reports_external_privileges()
    {
        var risk = new ScriptRiskAnalyzer().Analyze(new UserScriptParser().Parse(Source));
        Assert.True(risk.Level >= RiskLevel.High); Assert.Contains(risk.Findings, x => x.Contains("APIs sensíveis"));
    }

    [Fact]
    public void Panel_signature_analyzer_requires_creation_id_and_insertion_evidence()
    {
        const string source = "const ROOT='different-panel-root'; const host=document.createElement('div'); host.id=ROOT; host.attachShadow({mode:'open'}); document.body.appendChild(host); const game=document.getElementById('native'); game.id='native-menu';";
        var ids = new UserScriptPanelSignatureAnalyzer().FindOwnedElementIds(source);
        Assert.Equal(["different-panel-root"], ids);
    }

    [Fact]
    public void Panel_signature_analyzer_supports_direct_ids_and_rejects_uninserted_nodes()
    {
        const string source = "let panel=document.createElement('section');panel.id='random-dashboard';document.documentElement.append(panel);let orphan=document.createElement('aside');orphan.id='not-mounted';";
        var ids = new UserScriptPanelSignatureAnalyzer().FindOwnedElementIds(source);
        Assert.Equal(["random-dashboard"], ids);
    }
}
