using IdleForge.Core;
using IdleForge.Core.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace IdleForge.Core.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task Persists_games_profiles_and_scripts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idleforge-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var repository = new IdleForgeRepository(Path.Combine(directory, "test.db")); await repository.InitializeAsync();
            var game = new GameDefinition(Guid.NewGuid(), "Test Game", new("https://example.com/game"));
            var profile = new GameProfile(Guid.NewGuid(), game.Id, "Conta A", "profile-a");
            var script = new UserScript(Guid.NewGuid(), new UserScriptMetadata { Name = "Test" }, "// source", true, null, DateTimeOffset.UtcNow);
            await repository.SaveGameAsync(game); await repository.SaveProfileAsync(profile); await repository.SaveScriptAsync(script);
            Assert.Single(await repository.GetGamesAsync()); Assert.Single(await repository.GetProfilesAsync()); Assert.Single(await repository.GetScriptsAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Script_values_are_isolated_by_script_and_profile_under_concurrency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idleforge-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var repository = new IdleForgeRepository(Path.Combine(directory, "concurrent.db")); await repository.InitializeAsync();
            var script = Guid.NewGuid(); var profiles = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
            await Task.WhenAll(profiles.Select((profile, i) => repository.SetScriptValueAsync(script, profile, "owner", JsonSerializer.SerializeToElement(i))));
            var values = await Task.WhenAll(profiles.Select(profile => repository.GetScriptValuesAsync(script, profile)));
            for (var i = 0; i < values.Length; i++) Assert.Equal(i, values[i]["owner"].GetInt32());
            await repository.DeleteScriptValueAsync(script, profiles[0], "owner");
            Assert.Empty(await repository.GetScriptValuesAsync(script, profiles[0]));
            Assert.Equal(1, (await repository.GetScriptValuesAsync(script, profiles[1]))["owner"].GetInt32());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Updating_and_restoring_script_archives_versions_without_touching_gm_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idleforge-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var repository = new IdleForgeRepository(Path.Combine(directory, "versions.db")); await repository.InitializeAsync(); var id = Guid.NewGuid(); var profile = Guid.NewGuid();
            var first = new UserScript(id, new() { Name = "Versioned", Version = "1.0" }, "// one", true, null, DateTimeOffset.UtcNow); await repository.SaveScriptAsync(first); await repository.SetScriptValueAsync(id, profile, "learning", JsonSerializer.SerializeToElement(42));
            await repository.SaveScriptAsync(first with { Metadata = first.Metadata with { Version = "2.0" }, Source = "// two", UpdatedAt = DateTimeOffset.UtcNow });
            var archived = Assert.Single(await repository.GetScriptVersionsAsync(id)); Assert.Equal("1.0", archived.Version); Assert.Equal("// one", archived.Source); Assert.Equal(42, (await repository.GetScriptValuesAsync(id, profile))["learning"].GetInt32());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Script_activation_and_removal_are_isolated_per_profile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idleforge-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var repository = new IdleForgeRepository(Path.Combine(directory, "assignments.db")); await repository.InitializeAsync();
            var script = new UserScript(Guid.NewGuid(), new() { Name = "Scoped" }, "// scoped", true, null, DateTimeOffset.UtcNow); var principal = Guid.NewGuid(); var alt = Guid.NewGuid();
            await repository.SaveScriptAsync(script); await repository.SetScriptValueAsync(script.Id, principal, "learning", JsonSerializer.SerializeToElement(10)); await repository.SetScriptValueAsync(script.Id, alt, "learning", JsonSerializer.SerializeToElement(20));
            await repository.SetScriptEnabledForProfileAsync(script.Id, principal, false);
            Assert.False(Assert.Single(await repository.GetScriptsForProfileAsync(principal)).Enabled); Assert.True(Assert.Single(await repository.GetScriptsForProfileAsync(alt)).Enabled);
            Assert.Equal(10, (await repository.GetScriptValuesAsync(script.Id, principal))["learning"].GetInt32());
            await repository.DeleteScriptDataAsync(script.Id, principal);
            Assert.Empty(await repository.GetScriptValuesAsync(script.Id, principal)); Assert.Equal(20, (await repository.GetScriptValuesAsync(script.Id, alt))["learning"].GetInt32());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
