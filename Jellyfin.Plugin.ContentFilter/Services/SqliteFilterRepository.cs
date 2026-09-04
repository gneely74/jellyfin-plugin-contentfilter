using System.Globalization;
using Jellyfin.Plugin.ContentFilter.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Manages SQLite-backed persistence for media item content filters and cues.
/// </summary>
public sealed class SqliteFilterRepository : IDisposable
{
    private readonly ILogger<SqliteFilterRepository> _logger;
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteFilterRepository"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="databasePath">The full path to the SQLite database file.</param>
    public SqliteFilterRepository(ILogger<SqliteFilterRepository> logger, string databasePath)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeDatabase();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    private void InitializeDatabase()
    {
        lock (_writeLock)
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;

                    CREATE TABLE IF NOT EXISTS filters (
                        item_id TEXT PRIMARY KEY,
                        title TEXT NOT NULL,
                        year TEXT,
                        imdb_url TEXT,
                        source TEXT,
                        cue_count INTEGER NOT NULL DEFAULT 0,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS cues (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        item_id TEXT NOT NULL,
                        start_ms INTEGER NOT NULL,
                        end_ms INTEGER NOT NULL,
                        category TEXT NOT NULL,
                        channel TEXT NOT NULL,
                        action TEXT NOT NULL,
                        description TEXT,
                        FOREIGN KEY (item_id) REFERENCES filters(item_id) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_cues_item_id ON cues(item_id);
                    CREATE INDEX IF NOT EXISTS idx_cues_category ON cues(category);
                    """;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database for ContentFilter.");
                throw;
            }
        }
    }

    /// <summary>
    /// Checks if a filter exists in the database for the given item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns><see langword="true"/> if the filter exists; otherwise <see langword="false"/>.</returns>
    public bool HasFilter(Guid itemId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM filters WHERE item_id = @itemId LIMIT 1;";
        cmd.Parameters.AddWithValue("@itemId", itemId.ToString("N"));

        var result = cmd.ExecuteScalar();
        return result is not null;
    }

    /// <summary>
    /// Gets all item IDs that have active filters stored in the database.
    /// </summary>
    /// <returns>A hash set of item GUIDs.</returns>
    public HashSet<Guid> GetAllFilterItemIds()
    {
        var ids = new HashSet<Guid>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_id FROM filters;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var raw = reader.GetString(0);
            if (Guid.TryParse(raw, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Gets summary information (cue count) for all filters currently stored in the database.
    /// </summary>
    /// <returns>A dictionary mapping item GUID to cue count.</returns>
    public Dictionary<Guid, int> GetAllFilterSummaries()
    {
        var summaries = new Dictionary<Guid, int>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_id, cue_count FROM filters;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var raw = reader.GetString(0);
            var count = reader.GetInt32(1);
            if (Guid.TryParse(raw, out var id))
            {
                summaries[id] = count;
            }
        }

        return summaries;
    }

    /// <summary>
    /// Retrieves a filter and its cues from the database.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The parsed filter or <see langword="null"/> if not found.</returns>
    public JcfFilter? GetFilter(Guid itemId)
    {
        using var conn = CreateConnection();
        using var filterCmd = conn.CreateCommand();
        filterCmd.CommandText = "SELECT title, year, imdb_url, source FROM filters WHERE item_id = @itemId LIMIT 1;";
        filterCmd.Parameters.AddWithValue("@itemId", itemId.ToString("N"));

        string title = string.Empty;
        string year = string.Empty;
        string imdbUrl = string.Empty;
        string source = string.Empty;

        using (var reader = filterCmd.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            title = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            year = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            imdbUrl = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            source = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        }

        var filter = new JcfFilter
        {
            Title = title,
            Year = year,
            ImdbUrl = imdbUrl,
            Source = source
        };

        using var cuesCmd = conn.CreateCommand();
        cuesCmd.CommandText = """
            SELECT start_ms, end_ms, category, channel, action, description
            FROM cues
            WHERE item_id = @itemId
            ORDER BY start_ms ASC;
            """;
        cuesCmd.Parameters.AddWithValue("@itemId", itemId.ToString("N"));

        using (var cuesReader = cuesCmd.ExecuteReader())
        {
            while (cuesReader.Read())
            {
                var startMs = cuesReader.GetInt64(0);
                var endMs = cuesReader.GetInt64(1);
                var category = cuesReader.IsDBNull(2) ? string.Empty : cuesReader.GetString(2);
                var channel = cuesReader.IsDBNull(3) ? "both" : cuesReader.GetString(3);
                var action = cuesReader.IsDBNull(4) ? "none" : cuesReader.GetString(4);
                var description = cuesReader.IsDBNull(5) ? null : cuesReader.GetString(5);

                filter.Cues.Add(new FilterCue
                {
                    Start = TimeSpan.FromMilliseconds(startMs),
                    End = TimeSpan.FromMilliseconds(endMs),
                    Category = category,
                    Channel = channel,
                    Action = action,
                    Description = description
                });
            }
        }

        return filter;
    }

    /// <summary>
    /// Saves or updates a filter and replaces all of its cues in the database.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="filter">The filter document to save.</param>
    public void SaveFilter(Guid itemId, JcfFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_writeLock)
        {
            using var conn = CreateConnection();
            using var transaction = conn.BeginTransaction();

            using (var filterCmd = conn.CreateCommand())
            {
                filterCmd.Transaction = transaction;
                filterCmd.CommandText = """
                    INSERT INTO filters (item_id, title, year, imdb_url, source, cue_count, updated_at)
                    VALUES (@itemId, @title, @year, @imdbUrl, @source, @cueCount, @updatedAt)
                    ON CONFLICT(item_id) DO UPDATE SET
                        title = excluded.title,
                        year = excluded.year,
                        imdb_url = excluded.imdb_url,
                        source = excluded.source,
                        cue_count = excluded.cue_count,
                        updated_at = excluded.updated_at;
                    """;
                filterCmd.Parameters.AddWithValue("@itemId", itemId.ToString("N"));
                filterCmd.Parameters.AddWithValue("@title", filter.Title ?? string.Empty);
                filterCmd.Parameters.AddWithValue("@year", (object?)filter.Year ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@imdbUrl", (object?)filter.ImdbUrl ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@source", (object?)filter.Source ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@cueCount", filter.Cues.Count);
                filterCmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                filterCmd.ExecuteNonQuery();
            }

            using (var deleteCuesCmd = conn.CreateCommand())
            {
                deleteCuesCmd.Transaction = transaction;
                deleteCuesCmd.CommandText = "DELETE FROM cues WHERE item_id = @itemId;";
                deleteCuesCmd.Parameters.AddWithValue("@itemId", itemId.ToString("N"));
                deleteCuesCmd.ExecuteNonQuery();
            }

            if (filter.Cues.Count > 0)
            {
                using var insertCueCmd = conn.CreateCommand();
                insertCueCmd.Transaction = transaction;
                insertCueCmd.CommandText = """
                    INSERT INTO cues (item_id, start_ms, end_ms, category, channel, action, description)
                    VALUES (@itemId, @startMs, @endMs, @category, @channel, @action, @description);
                    """;

                var pItemId = insertCueCmd.Parameters.Add("@itemId", SqliteType.Text);
                var pStartMs = insertCueCmd.Parameters.Add("@startMs", SqliteType.Integer);
                var pEndMs = insertCueCmd.Parameters.Add("@endMs", SqliteType.Integer);
                var pCategory = insertCueCmd.Parameters.Add("@category", SqliteType.Text);
                var pChannel = insertCueCmd.Parameters.Add("@channel", SqliteType.Text);
                var pAction = insertCueCmd.Parameters.Add("@action", SqliteType.Text);
                var pDescription = insertCueCmd.Parameters.Add("@description", SqliteType.Text);

                pItemId.Value = itemId.ToString("N");

                foreach (var cue in filter.Cues)
                {
                    pStartMs.Value = (long)cue.Start.TotalMilliseconds;
                    pEndMs.Value = (long)cue.End.TotalMilliseconds;
                    pCategory.Value = cue.Category ?? string.Empty;
                    pChannel.Value = cue.Channel ?? "both";
                    pAction.Value = cue.Action ?? "none";
                    pDescription.Value = (object?)cue.Description ?? DBNull.Value;
                    insertCueCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Deletes a filter and all of its cues from the database.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    public void DeleteFilter(Guid itemId)
    {
        lock (_writeLock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM filters WHERE item_id = @itemId;";
            cmd.Parameters.AddWithValue("@itemId", itemId.ToString("N"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns total counts of filters and cues in the database.
    /// </summary>
    /// <returns>A tuple of total filters and total cues.</returns>
    public (int TotalFilters, int TotalCues) GetDatabaseStats()
    {
        using var conn = CreateConnection();
        using var cmdFilters = conn.CreateCommand();
        cmdFilters.CommandText = "SELECT COUNT(*) FROM filters;";
        var totalFilters = Convert.ToInt32(cmdFilters.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var cmdCues = conn.CreateCommand();
        cmdCues.CommandText = "SELECT COUNT(*) FROM cues;";
        var totalCues = Convert.ToInt32(cmdCues.ExecuteScalar(), CultureInfo.InvariantCulture);

        return (totalFilters, totalCues);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
