using System.Security.Cryptography;
using System.Text.Json;

namespace IdleForge.Core.Updates;

public sealed record UpdateFile(string Path, string Sha256, long Length);
public sealed record UpdateManifest(string Product, string Version, DateTimeOffset CreatedAt, IReadOnlyList<UpdateFile> Files);
public sealed record UpdatePlan(string InstallDirectory, string StagingDirectory, string BackupDirectory, string MainExecutable, int ParentProcessId, string HealthFile, string Token, int HealthTimeoutSeconds = 45);

public static class LocalUpdatePackage
{
    public const string ManifestName = "idleforge-update.json";
    public static async Task<UpdateManifest> ValidateAsync(string packageDirectory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packageDirectory); var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Manifesto de atualização não encontrado.", manifestPath);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Manifesto inválido.");
        if (manifest.Product != "IdleForge" || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Produto ou versão inválidos.");
        if (manifest.Files.Count == 0 || !manifest.Files.Any(x => x.Path.Equals("IdleForge.App.exe", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Pacote incompleto.");
        foreach (var item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested(); var full = Path.GetFullPath(Path.Combine(root, item.Path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException($"Arquivo ausente ou caminho inseguro: {item.Path}");
            var info = new FileInfo(full); if (info.Length != item.Length) throw new InvalidDataException($"Tamanho divergente: {item.Path}");
            await using var stream = File.OpenRead(full); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)); if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"SHA-256 divergente: {item.Path}");
        }
        return manifest;
    }

    public static async Task<UpdateManifest> CreateManifestAsync(string packageDirectory, string version, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packageDirectory); var files = new List<UpdateFile>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(x => !Path.GetFileName(x).Equals(ManifestName, StringComparison.OrdinalIgnoreCase)))
        {
            await using var stream = File.OpenRead(full); files.Add(new(Path.GetRelativePath(root, full).Replace('\\', '/'), Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), stream.Length));
        }
        var manifest = new UpdateManifest("IdleForge", version, DateTimeOffset.UtcNow, files.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray());
        await File.WriteAllTextAsync(Path.Combine(root, ManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken); return manifest;
    }
}
