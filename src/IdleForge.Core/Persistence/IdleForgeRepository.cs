using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace IdleForge.Core.Persistence;

public sealed class IdleForgeRepository(string databasePath)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        await using var source = new SqliteConnection(ConnectionString);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath }.ToString());
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS games(id TEXT PRIMARY KEY, name TEXT NOT NULL, start_uri TEXT NOT NULL, icon_path TEXT);
            CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY, game_id TEXT NOT NULL, name TEXT NOT NULL, browser_profile TEXT NOT NULL UNIQUE, keep_running INTEGER NOT NULL, last_opened TEXT, FOREIGN KEY(game_id) REFERENCES games(id));
            CREATE TABLE IF NOT EXISTS scripts(id TEXT PRIMARY KEY, metadata_json TEXT NOT NULL, source TEXT NOT NULL, enabled INTEGER NOT NULL, install_source TEXT, installed_at TEXT NOT NULL, updated_at TEXT);
            CREATE TABLE IF NOT EXISTS assignments(script_id TEXT NOT NULL, game_id TEXT, profile_id TEXT, enabled INTEGER NOT NULL, PRIMARY KEY(script_id, game_id, profile_id));
            CREATE TABLE IF NOT EXISTS script_values(script_id TEXT NOT NULL, profile_id TEXT NOT NULL, key TEXT NOT NULL, value_json TEXT, PRIMARY KEY(script_id, profile_id, key));
            CREATE TABLE IF NOT EXISTS script_versions(script_id TEXT NOT NULL, version TEXT NOT NULL, metadata_json TEXT NOT NULL, source TEXT NOT NULL, archived_at TEXT NOT NULL, PRIMARY KEY(script_id,version,archived_at));
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT, kind TEXT NOT NULL, message TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS script_panels(id TEXT PRIMARY KEY, script_id TEXT NOT NULL, profile_id TEXT NOT NULL, selector TEXT NOT NULL, label TEXT NOT NULL, automatic INTEGER NOT NULL, preference INTEGER NOT NULL, last_visible INTEGER NOT NULL, updated_at TEXT NOT NULL, UNIQUE(script_id,profile_id,selector));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "scripts", "broad_connect_approved", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "scripts", "main_world_approved", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "scripts", "local_source_path", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "scripts", "content_sha256", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "script_versions", "content_sha256", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "script_versions", "local_source_path", "TEXT", cancellationToken);
    }

    public async Task SaveGameAsync(GameDefinition game, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("INSERT OR REPLACE INTO games(id,name,start_uri,icon_path) VALUES($id,$name,$uri,$icon)",
            [("$id", game.Id.ToString()), ("$name", game.Name), ("$uri", game.StartUri.AbsoluteUri), ("$icon", game.IconPath)], cancellationToken);
    }

    public async Task SaveProfileAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("INSERT OR REPLACE INTO profiles(id,game_id,name,browser_profile,keep_running,last_opened) VALUES($id,$game,$name,$browser,$keep,$last)",
            [("$id", profile.Id.ToString()), ("$game", profile.GameId.ToString()), ("$name", profile.Name), ("$browser", profile.BrowserProfileName), ("$keep", profile.KeepRunning ? 1 : 0), ("$last", profile.LastOpenedAt?.ToString("O"))], cancellationToken);
    }

    public async Task SaveScriptAsync(UserScript script, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var archive = connection.CreateCommand(); archive.Transaction = transaction;
        archive.CommandText = "INSERT INTO script_versions(script_id,version,metadata_json,source,archived_at,content_sha256,local_source_path) SELECT id,json_extract(metadata_json,'$.Version'),metadata_json,source,$archived,content_sha256,local_source_path FROM scripts WHERE id=$id AND source<>$source";
        archive.Parameters.AddWithValue("$id", script.Id.ToString()); archive.Parameters.AddWithValue("$source", script.Source); archive.Parameters.AddWithValue("$archived", DateTimeOffset.UtcNow.ToString("O"));
        await archive.ExecuteNonQueryAsync(cancellationToken);
        var save = connection.CreateCommand(); save.Transaction = transaction;
        save.CommandText = "INSERT OR REPLACE INTO scripts(id,metadata_json,source,enabled,install_source,installed_at,updated_at,broad_connect_approved,main_world_approved,local_source_path,content_sha256) VALUES($id,$meta,$source,$enabled,$origin,$installed,$updated,$broad,$main,$local,$hash)";
        foreach (var pair in new (string, object?)[]
        {
            ("$id", script.Id.ToString()), ("$meta", JsonSerializer.Serialize(script.Metadata)), ("$source", script.Source), ("$enabled", script.Enabled ? 1 : 0),
            ("$origin", script.InstallSource?.AbsoluteUri), ("$installed", script.InstalledAt.ToString("O")), ("$updated", script.UpdatedAt?.ToString("O")),
            ("$broad", script.BroadConnectApproved ? 1 : 0), ("$main", script.MainWorldCompatibilityApproved ? 1 : 0),
            ("$local", script.LocalSourcePath), ("$hash", script.ContentSha256)
        }) save.Parameters.AddWithValue(pair.Item1, pair.Item2 ?? DBNull.Value);
        await save.ExecuteNonQueryAsync(cancellationToken);
        var trim = connection.CreateCommand(); trim.Transaction = transaction;
        trim.CommandText = "DELETE FROM script_versions WHERE script_id=$id AND rowid NOT IN (SELECT rowid FROM script_versions WHERE script_id=$id ORDER BY archived_at DESC LIMIT 10)";
        trim.Parameters.AddWithValue("$id", script.Id.ToString()); await trim.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>> GetScriptValuesAsync(Guid scriptId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT key,value_json FROM script_values WHERE script_id=$script AND profile_id=$profile";
        command.Parameters.AddWithValue("$script", scriptId.ToString()); command.Parameters.AddWithValue("$profile", profileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            result[reader.GetString(0)] = document.RootElement.Clone();
        }
        return result;
    }

    public Task SetScriptValueAsync(Guid scriptId, Guid profileId, string key, JsonElement value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 512) throw new ArgumentOutOfRangeException(nameof(key));
        var json = value.GetRawText();
        if (json.Length > 1_048_576) throw new ArgumentOutOfRangeException(nameof(value), "O valor GM excede 1 MiB.");
        return ExecuteAsync("INSERT INTO script_values(script_id,profile_id,key,value_json) VALUES($script,$profile,$key,$value) ON CONFLICT(script_id,profile_id,key) DO UPDATE SET value_json=excluded.value_json",
            [("$script", scriptId.ToString()), ("$profile", profileId.ToString()), ("$key", key), ("$value", json)], cancellationToken);
    }

    public Task DeleteScriptValueAsync(Guid scriptId, Guid profileId, string key, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM script_values WHERE script_id=$script AND profile_id=$profile AND key=$key",
            [("$script", scriptId.ToString()), ("$profile", profileId.ToString()), ("$key", key)], cancellationToken);

    public async Task<IReadOnlyList<GameDefinition>> GetGamesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<GameDefinition>();
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id,name,start_uri,icon_path FROM games ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), new(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return result;
    }

    public async Task<IReadOnlyList<GameProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<GameProfile>();
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id,game_id,name,browser_profile,keep_running,last_opened FROM profiles ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))));
        return result;
    }

    public async Task<IReadOnlyList<UserScript>> GetScriptsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<UserScript>();
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id,metadata_json,source,enabled,install_source,installed_at,updated_at,broad_connect_approved,main_world_approved,local_source_path,content_sha256 FROM scripts ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<UserScriptMetadata>(reader.GetString(1))!, reader.GetString(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : new Uri(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)), reader.GetBoolean(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return result;
    }

    public async Task<IReadOnlyList<UserScript>> GetScriptsForProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var scripts = await GetScriptsAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT script_id,enabled FROM assignments WHERE profile_id=$profile AND game_id=''"; command.Parameters.AddWithValue("$profile", profileId.ToString());
        var overrides = new Dictionary<Guid, bool>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) overrides[Guid.Parse(reader.GetString(0))] = reader.GetBoolean(1);
        return scripts.Select(x => x with { Enabled = overrides.GetValueOrDefault(x.Id, x.Enabled) }).ToArray();
    }

    public Task SetScriptEnabledForProfileAsync(Guid scriptId, Guid profileId, bool enabled, CancellationToken cancellationToken = default) => ExecuteAsync(
        "INSERT INTO assignments(script_id,game_id,profile_id,enabled) VALUES($script,'',$profile,$enabled) ON CONFLICT(script_id,game_id,profile_id) DO UPDATE SET enabled=excluded.enabled",
        [("$script", scriptId.ToString()), ("$profile", profileId.ToString()), ("$enabled", enabled ? 1 : 0)], cancellationToken);

    public Task RemoveScriptFromProfileAsync(Guid scriptId, Guid profileId, CancellationToken cancellationToken = default) =>
        SetScriptEnabledForProfileAsync(scriptId, profileId, false, cancellationToken);

    public Task DeleteScriptDataAsync(Guid scriptId, Guid profileId, CancellationToken cancellationToken = default) => ExecuteAsync(
        "DELETE FROM script_values WHERE script_id=$script AND profile_id=$profile; DELETE FROM script_panels WHERE script_id=$script AND profile_id=$profile",
        [("$script", scriptId.ToString()), ("$profile", profileId.ToString())], cancellationToken);

    public async Task<IReadOnlyList<UserScriptVersion>> GetScriptVersionsAsync(Guid scriptId, CancellationToken cancellationToken = default)
    {
        var result = new List<UserScriptVersion>(); await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT version,metadata_json,source,archived_at,content_sha256,local_source_path FROM script_versions WHERE script_id=$id ORDER BY archived_at DESC"; command.Parameters.AddWithValue("$id", scriptId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(scriptId, reader.GetString(0), JsonSerializer.Deserialize<UserScriptMetadata>(reader.GetString(1))!, reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    public async Task<IReadOnlyList<ScriptPanelBinding>> GetPanelBindingsAsync(Guid profileId, Guid? scriptId = null, CancellationToken cancellationToken = default)
    {
        var result = new List<ScriptPanelBinding>(); await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id,script_id,profile_id,selector,label,automatic,preference,last_visible,updated_at FROM script_panels WHERE profile_id=$profile AND ($script IS NULL OR script_id=$script) ORDER BY label";
        command.Parameters.AddWithValue("$profile", profileId.ToString()); command.Parameters.AddWithValue("$script", scriptId?.ToString() ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), (PanelVisibilityPreference)reader.GetInt32(6), reader.GetBoolean(7), DateTimeOffset.Parse(reader.GetString(8))));
        return result;
    }

    public Task SavePanelBindingAsync(ScriptPanelBinding binding, CancellationToken cancellationToken = default) => ExecuteAsync(
        "INSERT INTO script_panels(id,script_id,profile_id,selector,label,automatic,preference,last_visible,updated_at) VALUES($id,$script,$profile,$selector,$label,$automatic,$preference,$visible,$updated) ON CONFLICT(script_id,profile_id,selector) DO UPDATE SET label=excluded.label,automatic=excluded.automatic,preference=excluded.preference,last_visible=excluded.last_visible,updated_at=excluded.updated_at",
        [("$id", binding.Id.ToString()), ("$script", binding.ScriptId.ToString()), ("$profile", binding.ProfileId.ToString()), ("$selector", binding.Selector), ("$label", binding.Label), ("$automatic", binding.AutomaticallyDetected ? 1 : 0), ("$preference", (int)binding.Preference), ("$visible", binding.LastVisible ? 1 : 0), ("$updated", binding.UpdatedAt.ToString("O"))], cancellationToken);

    public Task DeletePanelBindingAsync(Guid id, CancellationToken cancellationToken = default) => ExecuteAsync("DELETE FROM script_panels WHERE id=$id", [("$id", id.ToString())], cancellationToken);

    private async Task ExecuteAsync(string sql, IEnumerable<(string Name, object? Value)> parameters, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var inspect = connection.CreateCommand(); inspect.CommandText = $"PRAGMA table_info({table})";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
