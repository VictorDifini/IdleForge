using IdleForge.Core.Persistence;
using System.Security.Cryptography;
using System.Text;

namespace IdleForge.Core.UserScripts;

public enum LocalScriptUpdateState { UpToDate, UpdateAvailable, FileNotFound, NotLinked, InvalidFile, ReadError }

public sealed record LocalScriptUpdateCheck(
    UserScript Script,
    LocalScriptUpdateState State,
    string? LocalPath,
    string InstalledHash,
    string? LocalHash = null,
    UserScriptMetadata? LocalMetadata = null,
    string? Error = null)
{
    public bool CanUpdate => State == LocalScriptUpdateState.UpdateAvailable;
    public bool PossibleDowngrade => LocalMetadata is not null && CompareVersions(LocalMetadata.Version, Script.Metadata.Version) < 0;
    private static int CompareVersions(string left, string right) =>
        Version.TryParse(left, out var a) && Version.TryParse(right, out var b) ? a.CompareTo(b) : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
}

public sealed class LocalScriptUpdateService(IdleForgeRepository repository)
{
    public static string ComputeHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    public async Task<IReadOnlyList<LocalScriptUpdateCheck>> CheckAsync(IEnumerable<UserScript> scripts, CancellationToken cancellationToken = default)
    {
        var checks = scripts.Select(script => CheckOneAsync(script, cancellationToken));
        return await Task.WhenAll(checks);
    }

    public async Task<LocalScriptUpdateCheck> CheckOneAsync(UserScript script, CancellationToken cancellationToken = default)
    {
        var installedHash = string.IsNullOrWhiteSpace(script.ContentSha256) ? ComputeHash(script.Source) : script.ContentSha256;
        var path = script.LocalSourcePath;
        if (string.IsNullOrWhiteSpace(path) && script.InstallSource is { IsFile: true }) path = script.InstallSource.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return new(script, LocalScriptUpdateState.NotLinked, null, installedHash);
        if (!File.Exists(path)) return new(script, LocalScriptUpdateState.FileNotFound, path, installedHash);
        try
        {
            var snapshot = await ReadStableAsync(path, cancellationToken);
            UserScriptMetadata metadata;
            try { metadata = new UserScriptParser().Parse(snapshot.Source); }
            catch (Exception ex) { return new(script, LocalScriptUpdateState.InvalidFile, path, installedHash, snapshot.Hash, Error: ex.Message); }
            if (!string.Equals(metadata.Name, script.Metadata.Name, StringComparison.Ordinal) || !string.Equals(metadata.Namespace, script.Metadata.Namespace, StringComparison.Ordinal))
                return new(script, LocalScriptUpdateState.InvalidFile, path, installedHash, snapshot.Hash, metadata, "O @name ou @namespace não corresponde ao script vinculado.");
            return new(script, snapshot.Hash == installedHash ? LocalScriptUpdateState.UpToDate : LocalScriptUpdateState.UpdateAvailable, path, installedHash, snapshot.Hash, metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(script, LocalScriptUpdateState.ReadError, path, installedHash, Error: ex.Message);
        }
    }

    public async Task<UserScript> LinkAsync(UserScript script, string path, CancellationToken cancellationToken = default)
    {
        var check = await CheckOneAsync(script with { LocalSourcePath = path }, cancellationToken);
        if (check.State == LocalScriptUpdateState.InvalidFile) throw new InvalidDataException(check.Error);
        if (check.State is LocalScriptUpdateState.FileNotFound or LocalScriptUpdateState.ReadError) throw new IOException(check.Error ?? "Não foi possível ler o arquivo.");
        var linked = script with { LocalSourcePath = Path.GetFullPath(path), ContentSha256 = ComputeHash(script.Source), InstallSource = new Uri(Path.GetFullPath(path)) };
        await repository.SaveScriptAsync(linked, cancellationToken);
        return linked;
    }

    public async Task<UserScript> ApplyAsync(LocalScriptUpdateCheck expected, bool broadConnectApproved, bool mainWorldApproved, CancellationToken cancellationToken = default)
    {
        if (!expected.CanUpdate || string.IsNullOrWhiteSpace(expected.LocalPath) || string.IsNullOrWhiteSpace(expected.LocalHash)) throw new InvalidOperationException("Esta verificação não contém uma atualização aplicável.");
        var snapshot = await ReadStableAsync(expected.LocalPath, cancellationToken);
        if (!string.Equals(snapshot.Hash, expected.LocalHash, StringComparison.Ordinal)) throw new InvalidOperationException("O arquivo foi modificado após a verificação. Verifique novamente antes de atualizar.");
        var metadata = new UserScriptParser().Parse(snapshot.Source);
        if (!string.Equals(metadata.Name, expected.Script.Metadata.Name, StringComparison.Ordinal) || !string.Equals(metadata.Namespace, expected.Script.Metadata.Namespace, StringComparison.Ordinal))
            throw new InvalidDataException("O arquivo não corresponde ao userscript vinculado.");
        var updated = expected.Script with
        {
            Metadata = metadata, Source = snapshot.Source, UpdatedAt = DateTimeOffset.UtcNow,
            LocalSourcePath = Path.GetFullPath(expected.LocalPath), InstallSource = new Uri(Path.GetFullPath(expected.LocalPath)),
            ContentSha256 = snapshot.Hash, BroadConnectApproved = broadConnectApproved, MainWorldCompatibilityApproved = mainWorldApproved
        };
        await repository.SaveScriptAsync(updated, cancellationToken);
        return updated;
    }

    public static IReadOnlyList<string> PermissionChanges(UserScriptMetadata oldMetadata, UserScriptMetadata newMetadata)
    {
        static IEnumerable<string> Added(IEnumerable<string> before, IEnumerable<string> after, string label) =>
            after.Except(before, StringComparer.OrdinalIgnoreCase).Select(x => $"{label}: {x}");
        return Added(oldMetadata.Grants, newMetadata.Grants, "@grant")
            .Concat(Added(oldMetadata.Connect, newMetadata.Connect, "@connect"))
            .Concat(Added(oldMetadata.Matches, newMetadata.Matches, "@match"))
            .Concat(Added(oldMetadata.Includes, newMetadata.Includes, "@include"))
            .Concat(!string.Equals(oldMetadata.Sandbox, newMetadata.Sandbox, StringComparison.OrdinalIgnoreCase) ? [$"@sandbox: {oldMetadata.Sandbox ?? "(padrão)"} → {newMetadata.Sandbox ?? "(padrão)"}"] : [])
            .ToArray();
    }

    private static async Task<(string Source, string Hash)> ReadStableAsync(string path, CancellationToken cancellationToken)
    {
        var before = new FileInfo(path); var length = before.Length; var written = before.LastWriteTimeUtc;
        string source;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            source = await reader.ReadToEndAsync(cancellationToken);
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc != written) throw new IOException("O arquivo mudou durante a leitura. Tente verificar novamente.");
        return (source, ComputeHash(source));
    }
}
