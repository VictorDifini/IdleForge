using IdleForge.Core.Updates;
using System.Diagnostics;
using System.Text.Json;

if (args.Length != 2 || args[0] != "--plan") return 2;
var planPath = Path.GetFullPath(args[1]); var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidDataException("Plano inválido.");
var install = Path.GetFullPath(plan.InstallDirectory); var staging = Path.GetFullPath(plan.StagingDirectory); var backup = Path.GetFullPath(plan.BackupDirectory); var health = Path.GetFullPath(plan.HealthFile);
if (install == staging || install == backup || staging == backup || !Directory.Exists(staging)) throw new InvalidDataException("Diretórios de atualização inseguros.");
await LocalUpdatePackage.ValidateAsync(staging);
try { Process.GetProcessById(plan.ParentProcessId).WaitForExit(30000); } catch (ArgumentException) { }
Directory.CreateDirectory(backup);
Process? child = null;
try
{
    MoveContents(install, backup); var oldVersion = FileVersionInfo.GetVersionInfo(Path.Combine(backup, plan.MainExecutable)).FileVersion ?? "0.0.0"; await LocalUpdatePackage.CreateManifestAsync(backup, Version.TryParse(oldVersion, out var parsedOld) ? parsedOld.ToString(3) : "0.0.0"); CopyContents(staging, install, skipManifest: true); if (File.Exists(health)) File.Delete(health);
    var exe = Path.Combine(install, plan.MainExecutable); child = Process.Start(new ProcessStartInfo(exe, $"--update-health-token {plan.Token} --update-health-file \"{health}\"") { UseShellExecute = true }) ?? throw new InvalidOperationException("Não foi possível reiniciar o IdleForge.");
    var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(plan.HealthTimeoutSeconds, 2, 120)); while (DateTime.UtcNow < deadline && !File.Exists(health) && !child.HasExited) await Task.Delay(500);
    if (!File.Exists(health) || !string.Equals((await File.ReadAllTextAsync(health)).Trim(), plan.Token, StringComparison.Ordinal)) throw new InvalidOperationException("A nova versão não confirmou a inicialização.");
    AppendHistory("installed", install, backup, null); return 0;
}
catch (Exception ex)
{
    try { if (child is { HasExited: false }) { child.Kill(entireProcessTree: true); child.WaitForExit(10000); } MoveContents(install, staging + ".failed-" + plan.Token); MoveContents(backup, install); Process.Start(new ProcessStartInfo(Path.Combine(install, plan.MainExecutable), "--update-rollback") { UseShellExecute = true }); AppendHistory("rolled-back", install, backup, ex.Message); } catch (Exception rollback) { AppendHistory("rollback-failed", install, backup, ex + " | " + rollback); }
    return 1;
}

static void MoveContents(string source, string destination)
{
    Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source)) File.Move(file, Path.Combine(destination, Path.GetFileName(file)), true);
    foreach (var dir in Directory.EnumerateDirectories(source)) { var target = Path.Combine(destination, Path.GetFileName(dir)); if (Directory.Exists(target)) Directory.Delete(target, true); Directory.Move(dir, target); }
}
static void CopyContents(string source, string destination, bool skipManifest)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { if (skipManifest && Path.GetFileName(file) == LocalUpdatePackage.ManifestName) continue; var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true); }
}
static void AppendHistory(string state, string install, string backup, string? error)
{
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleForge"); Directory.CreateDirectory(data); File.AppendAllText(Path.Combine(data, "update-history.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, state, install, backup, error }) + Environment.NewLine);
}
