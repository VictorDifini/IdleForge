namespace IdleForge.Core;

public sealed record GameDefinition(Guid Id, string Name, Uri StartUri, string? IconPath = null);

public sealed record GameProfile(
    Guid Id,
    Guid GameId,
    string Name,
    string BrowserProfileName,
    bool KeepRunning = true,
    DateTimeOffset? LastOpenedAt = null);

public enum ScriptRunAt { DocumentStart, DocumentBody, DocumentEnd, DocumentIdle }

public sealed record UserScriptMetadata
{
    public string Name { get; init; } = "Script sem nome";
    public string? Namespace { get; init; }
    public string Version { get; init; } = "0.0.0";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public IReadOnlyList<string> Matches { get; init; } = [];
    public IReadOnlyList<string> Includes { get; init; } = [];
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public IReadOnlyList<string> Grants { get; init; } = [];
    public IReadOnlyList<Uri> Requires { get; init; } = [];
    public IReadOnlyDictionary<string, Uri> Resources { get; init; } = new Dictionary<string, Uri>();
    public IReadOnlyList<string> Connect { get; init; } = [];
    public Uri? UpdateUrl { get; init; }
    public Uri? DownloadUrl { get; init; }
    public ScriptRunAt RunAt { get; init; } = ScriptRunAt.DocumentIdle;
    public string? Sandbox { get; init; }
    public bool NoFrames { get; init; }
}

public sealed record UserScript(
    Guid Id,
    UserScriptMetadata Metadata,
    string Source,
    bool Enabled,
    Uri? InstallSource,
    DateTimeOffset InstalledAt,
    DateTimeOffset? UpdatedAt = null,
    bool BroadConnectApproved = false,
    bool MainWorldCompatibilityApproved = false,
    string? LocalSourcePath = null,
    string? ContentSha256 = null);

public sealed record ScriptAssignment(Guid ScriptId, Guid? GameId, Guid? ProfileId, bool Enabled);
public sealed record UserScriptVersion(Guid ScriptId, string Version, UserScriptMetadata Metadata, string Source, DateTimeOffset ArchivedAt, string? ContentSha256 = null, string? LocalSourcePath = null);

public enum RiskLevel { Low, Medium, High, Critical }

public sealed record ScriptRisk(RiskLevel Level, IReadOnlyList<string> Findings);
