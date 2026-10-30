using IdleForge.Core.Persistence;
using IdleForge.Core.UserScripts;
using Microsoft.Data.Sqlite;

namespace IdleForge.Core.Tests;

public sealed class LocalScriptUpdateServiceTests
{
    private const string Header = "// ==UserScript==\n// @name Local Test\n// @namespace idleforge-tests\n// @version {0}\n// @match https://example.com/*\n{1}// ==/UserScript==\n";
    private static string Source(string version, string body = "console.log(1);", string extra = "") => string.Format(Header, version, extra) + body;

    [Fact]
    public async Task Identical_changed_same_version_missing_and_unlinked_are_distinguished()
    {
        var root = NewRoot(); try
        {
            var repo = await Repository(root); var file = Path.Combine(root, "local.user.js"); var original = Source("1.0");
            await File.WriteAllTextAsync(file, original);
            var script = Script(original, file); await repo.SaveScriptAsync(script);
            var service = new LocalScriptUpdateService(repo);
            Assert.Equal(LocalScriptUpdateState.UpToDate, (await service.CheckOneAsync(script)).State);
            await File.WriteAllTextAsync(file, Source("1.0", "console.log(2);"));
            var changed = await service.CheckOneAsync(script); Assert.Equal(LocalScriptUpdateState.UpdateAvailable, changed.State); Assert.Equal("1.0", changed.LocalMetadata!.Version);
            File.Delete(file); Assert.Equal(LocalScriptUpdateState.FileNotFound, (await service.CheckOneAsync(script)).State);
            Assert.Equal(LocalScriptUpdateState.NotLinked, (await service.CheckOneAsync(script with { LocalSourcePath = null, InstallSource = null })).State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Invalid_or_wrong_identity_is_rejected()
    {
        var root = NewRoot(); try
        {
            var repo = await Repository(root); var file = Path.Combine(root, "bad.user.js"); var original = Source("1.0"); var script = Script(original, file);
            await File.WriteAllTextAsync(file, "not a userscript");
            Assert.Equal(LocalScriptUpdateState.InvalidFile, (await new LocalScriptUpdateService(repo).CheckOneAsync(script)).State);
            await File.WriteAllTextAsync(file, original.Replace("Local Test", "Other"));
            Assert.Equal(LocalScriptUpdateState.InvalidFile, (await new LocalScriptUpdateService(repo).CheckOneAsync(script)).State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Apply_requires_the_verified_hash_and_archives_atomically()
    {
        var root = NewRoot(); try
        {
            var repo = await Repository(root); var file = Path.Combine(root, "local.user.js"); var original = Source("1.0"); var script = Script(original, file); await repo.SaveScriptAsync(script);
            await File.WriteAllTextAsync(file, Source("1.1", "console.log(2);"));
            var service = new LocalScriptUpdateService(repo); var check = await service.CheckOneAsync(script);
            await File.WriteAllTextAsync(file, Source("1.2", "console.log(3);"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(check, false, false));
            Assert.Equal(original, Assert.Single(await repo.GetScriptsAsync()).Source);
            check = await service.CheckOneAsync(script);
            var updated = await service.ApplyAsync(check, false, false);
            Assert.Equal("1.2", updated.Metadata.Version); Assert.Equal(check.LocalHash, updated.ContentSha256);
            Assert.Equal(original, Assert.Single(await repo.GetScriptVersionsAsync(script.Id)).Source);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Link_persists_path_without_replacing_installed_code()
    {
        var root = NewRoot(); try
        {
            var repo = await Repository(root); var file = Path.Combine(root, "moved.user.js"); var original = Source("1.0"); await File.WriteAllTextAsync(file, Source("1.1"));
            var script = Script(original, null); await repo.SaveScriptAsync(script);
            var linked = await new LocalScriptUpdateService(repo).LinkAsync(script, file);
            Assert.Equal(Path.GetFullPath(file), linked.LocalSourcePath); Assert.Equal(original, linked.Source);
            Assert.Equal(LocalScriptUpdateState.UpdateAvailable, (await new LocalScriptUpdateService(repo).CheckOneAsync(linked)).State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Permission_changes_and_downgrade_are_reported_without_execution()
    {
        var root = NewRoot(); try
        {
            var repo = await Repository(root); var file = Path.Combine(root, "local.user.js"); var original = Source("2.0"); var script = Script(original, file);
            await File.WriteAllTextAsync(file, Source("1.0", extra: "// @grant GM_download\n"));
            var check = await new LocalScriptUpdateService(repo).CheckOneAsync(script);
            Assert.True(check.PossibleDowngrade);
            Assert.Contains(LocalScriptUpdateService.PermissionChanges(script.Metadata, check.LocalMetadata!), x => x.Contains("GM_download"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Migration_preserves_existing_scripts_and_profile_assignments()
    {
        var root = NewRoot(); try
        {
            var db = Path.Combine(root, "old.db");
            await using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE scripts(id TEXT PRIMARY KEY, metadata_json TEXT NOT NULL, source TEXT NOT NULL, enabled INTEGER NOT NULL, install_source TEXT, installed_at TEXT NOT NULL, updated_at TEXT, broad_connect_approved INTEGER NOT NULL DEFAULT 0, main_world_approved INTEGER NOT NULL DEFAULT 0); CREATE TABLE assignments(script_id TEXT NOT NULL, game_id TEXT, profile_id TEXT, enabled INTEGER NOT NULL, PRIMARY KEY(script_id,game_id,profile_id));";
                await command.ExecuteNonQueryAsync();
            }
            var repo = new IdleForgeRepository(db); await repo.InitializeAsync();
            var script = Script(Source("1.0"), null); await repo.SaveScriptAsync(script); var profile = Guid.NewGuid(); await repo.SetScriptEnabledForProfileAsync(script.Id, profile, false);
            Assert.False(Assert.Single(await repo.GetScriptsForProfileAsync(profile)).Enabled);
        }
        finally { Cleanup(root); }
    }

    private static UserScript Script(string source, string? path)
    {
        var metadata = new UserScriptParser().Parse(source);
        return new(Guid.NewGuid(), metadata, source, true, path is null ? null : new Uri(path), DateTimeOffset.UtcNow, LocalSourcePath: path, ContentSha256: LocalScriptUpdateService.ComputeHash(source));
    }
    private static string NewRoot() { var path = Path.Combine(Path.GetTempPath(), "idleforge-local-update-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task<IdleForgeRepository> Repository(string root) { var repo = new IdleForgeRepository(Path.Combine(root, "test.db")); await repo.InitializeAsync(); return repo; }
    private static void Cleanup(string root) { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
