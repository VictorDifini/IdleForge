using IdleForge.Core.Updates;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace IdleForge.App.Updates;

public sealed record IdleForgeSettings(bool RestoreLastSession = true, bool RestoreProfiles = true, bool RestoreLayout = true, bool RememberScriptPreferences = true, string? UpdateFolder = null);

public sealed class UpdateCoordinator
{
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleForge");
    public string SettingsPath => Path.Combine(_dataRoot, "settings.json");
    public IdleForgeSettings LoadSettings() { try { return JsonSerializer.Deserialize<IdleForgeSettings>(File.ReadAllText(SettingsPath)) ?? new(); } catch { return new(); } }
    public async Task SaveSettingsAsync(IdleForgeSettings value) => await AtomicWriteAsync(SettingsPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    public async Task<(string Directory, UpdateManifest Manifest)?> FindNewestAsync(string root)
    {
        if (!Directory.Exists(root)) return null; var candidates = new List<(string, UpdateManifest)>();
        foreach (var directory in new[] { root }.Concat(Directory.EnumerateDirectories(root))) try { candidates.Add((directory, await LocalUpdatePackage.ValidateAsync(directory))); } catch { }
        return candidates.OrderByDescending(x => Version.Parse(x.Item2.Version)).FirstOrDefault() is { } best && best.Item2 is not null ? best : null;
    }

    public async Task LaunchUpdateAsync(string packageDirectory, Func<Task> saveState)
    {
        var manifest = await LocalUpdatePackage.ValidateAsync(packageDirectory); await saveState(); var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(_dataRoot, "Updates", "staging-" + token); CopyDirectory(packageDirectory, staging); await LocalUpdatePackage.ValidateAsync(staging);
        var updaterSource = Path.Combine(AppContext.BaseDirectory, "IdleForge.Updater.exe"); if (!File.Exists(updaterSource)) throw new FileNotFoundException("IdleForge.Updater.exe não foi incluído nesta instalação.", updaterSource);
        var updaterRun = Path.Combine(_dataRoot, "Updates", "runner-" + token + ".exe"); Directory.CreateDirectory(Path.GetDirectoryName(updaterRun)!); File.Copy(updaterSource, updaterRun, true);
        var backup = Path.Combine(_dataRoot, "UpdateBackups", $"{GetCurrentVersion()}-{DateTime.UtcNow:yyyyMMdd-HHmmss}"); var health = Path.Combine(_dataRoot, "Updates", "health-" + token + ".ok");
        var plan = new UpdatePlan(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), staging, backup, "IdleForge.App.exe", Environment.ProcessId, health, token); var planPath = Path.Combine(_dataRoot, "Updates", "plan-" + token + ".json"); await AtomicWriteAsync(planPath, JsonSerializer.Serialize(plan));
        Process.Start(new ProcessStartInfo(updaterRun, $"--plan \"{planPath}\"") { UseShellExecute = true }); System.Windows.Application.Current.Shutdown();
    }
    public string GetCurrentVersion() => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    public IEnumerable<string> Backups() { var root = Path.Combine(_dataRoot, "UpdateBackups"); return Directory.Exists(root) ? Directory.EnumerateDirectories(root).OrderByDescending(x => x) : []; }
    private static void CopyDirectory(string source, string destination) { foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true); } }
    public static async Task AtomicWriteAsync(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp"; await File.WriteAllTextAsync(temp, content); if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true); else File.Move(temp, path); }
}
