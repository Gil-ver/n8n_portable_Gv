using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace n8n_launcher_Gv;

internal sealed class N8nExecutionListService
{
    private const int CurrentSchemaVersion = 1;
    private const string CacheFileName = "execution_list_cache.json";
    private static readonly TimeSpan CacheWindow = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<N8nExecutionListCacheEntry?> ReadCacheAsync(string stateDir, CancellationToken cancellationToken = default)
    {
        string cachePath = GetCachePath(stateDir);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var entry = await JsonSerializer.DeserializeAsync<N8nExecutionListCacheEntry>(stream, JsonOptions, cancellationToken);
            if (entry?.SchemaVersion != CurrentSchemaVersion || entry.Records is null)
            {
                Debug.WriteLine($"[ExecutionListCache] Ignore incompatible cache: {cachePath}");
                return null;
            }

            entry.Records = NormalizeRecords(entry.Records);
            return entry;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionListCache] Read failed: {ex.Message}");
            return null;
        }
    }

    public async Task WriteCacheAsync(
        string stateDir,
        IReadOnlyList<N8nExecutionListRecord> records,
        DateTimeOffset? databaseLastWriteTimeUtc,
        CancellationToken cancellationToken = default)
    {
        string cachePath = GetCachePath(stateDir);
        string? cacheDir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        var entry = new N8nExecutionListCacheEntry
        {
            SchemaVersion = CurrentSchemaVersion,
            CachedAt = DateTimeOffset.Now,
            DatabaseLastWriteTimeUtc = databaseLastWriteTimeUtc,
            Records = NormalizeRecords(records)
        };

        string tempPath = cachePath + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionListCache] Write failed: {ex.Message}");
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                Debug.WriteLine($"[ExecutionListCache] Temp cleanup failed: {cleanupEx.Message}");
            }
        }
    }

    public async Task<N8nExecutionListSyncResult> SyncLast7DaysAsync(
        string stateDir,
        string portableRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            N8nExecutionListCacheEntry? cache = await ReadCacheAsync(stateDir, cancellationToken);
            IReadOnlyList<N8nExecutionListRecord> databaseRecords = await ReadLast7DaysFromDatabaseAsync(portableRoot, cancellationToken);
            IReadOnlyList<N8nExecutionListRecord> mergedRecords = MergeAndTrim(cache?.Records, databaseRecords);
            DateTimeOffset? databaseLastWriteTimeUtc = N8nExecutionStatsService.GetDatabaseLastWriteTimeUtc(portableRoot);

            await WriteCacheAsync(stateDir, mergedRecords, databaseLastWriteTimeUtc, cancellationToken);
            return new N8nExecutionListSyncResult(mergedRecords, null, databaseLastWriteTimeUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionList] Sync failed: {ex.Message}");
            N8nExecutionListCacheEntry? cache = await ReadCacheAsync(stateDir, cancellationToken);
            IReadOnlyList<N8nExecutionListRecord> fallbackRecords = cache?.Records ?? new List<N8nExecutionListRecord>();
            return new N8nExecutionListSyncResult(fallbackRecords, ex.Message, cache?.DatabaseLastWriteTimeUtc);
        }
    }

    public async Task<IReadOnlyList<N8nExecutionListRecord>> ReadLast7DaysFromDatabaseAsync(
        string portableRoot,
        CancellationToken cancellationToken = default)
    {
        string databasePath = Path.Combine(portableRoot, "data", ".n8n", "database.sqlite");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException($"SQLite database not found: {databasePath}", databasePath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 2
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = BuildExecutionListSql();

        var records = new List<N8nExecutionListRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string executionId = ReadString(reader, "exec_id");
            if (string.IsNullOrWhiteSpace(executionId))
            {
                continue;
            }

            string? workflowId = ReadNullableString(reader, "workflow_id");
            string workflowName = ReadNullableString(reader, "workflow_name")?.Trim() ?? "Unknown workflow";
            string status = ReadNullableString(reader, "status")?.Trim() ?? "unknown";
            DateTimeOffset? startedAt = TryReadDateTimeOffset(reader, "startedAt");
            DateTimeOffset? stoppedAt = TryReadDateTimeOffset(reader, "stoppedAt");
            double? runTimeSeconds = TryReadDouble(reader, "run_time_secs");

            if (runTimeSeconds is null && startedAt is not null && stoppedAt is not null)
            {
                runTimeSeconds = Math.Max(0, (stoppedAt.Value - startedAt.Value).TotalSeconds);
            }

            records.Add(new N8nExecutionListRecord(
                executionId,
                workflowId,
                workflowName,
                status,
                startedAt,
                stoppedAt,
                runTimeSeconds));
        }

        return NormalizeRecords(records);
    }

    public static string GetCachePath(string stateDir)
    {
        return Path.Combine(stateDir, CacheFileName);
    }

    private static IReadOnlyList<N8nExecutionListRecord> MergeAndTrim(
        IReadOnlyList<N8nExecutionListRecord>? cachedRecords,
        IReadOnlyList<N8nExecutionListRecord> databaseRecords)
    {
        var map = new Dictionary<string, N8nExecutionListRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (N8nExecutionListRecord record in cachedRecords ?? Array.Empty<N8nExecutionListRecord>())
        {
            if (!string.IsNullOrWhiteSpace(record.ExecutionId))
            {
                map[record.ExecutionId] = record;
            }
        }

        foreach (N8nExecutionListRecord record in databaseRecords)
        {
            if (!string.IsNullOrWhiteSpace(record.ExecutionId))
            {
                map[record.ExecutionId] = record;
            }
        }

        return NormalizeRecords(map.Values);
    }

    private static List<N8nExecutionListRecord> NormalizeRecords(IEnumerable<N8nExecutionListRecord> records)
    {
        DateTimeOffset cutoff = DateTimeOffset.Now.Subtract(CacheWindow);

        return records
            .Where(static record => !string.IsNullOrWhiteSpace(record.ExecutionId))
            .Where(record => record.StartedAt is null || record.StartedAt.Value.ToLocalTime() >= cutoff)
            .GroupBy(static record => record.ExecutionId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderByDescending(static record => record.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static record => TryParseExecutionId(record.ExecutionId))
            .ThenByDescending(static record => record.ExecutionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long TryParseExecutionId(string executionId)
    {
        return long.TryParse(executionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : long.MinValue;
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        object value = reader[name];
        return value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        object value = reader[name];
        string? text = value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static double? TryReadDouble(SqliteDataReader reader, string name)
    {
        object value = reader[name];
        if (value == DBNull.Value)
        {
            return null;
        }

        try
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(number) || double.IsInfinity(number) ? null : Math.Max(0, number);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        string? text = ReadNullableString(reader, name);
        if (text is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value)
            ? value
            : null;
    }

    private static string BuildExecutionListSql()
    {
        return """
               SELECT
                   CAST(e.id AS TEXT) AS exec_id,
                   CAST(e.workflowId AS TEXT) AS workflow_id,
                   w.name AS workflow_name,
                   e.status,
                   e.startedAt,
                   e.stoppedAt,
                   CASE
                       WHEN e.stoppedAt IS NOT NULL AND e.startedAt IS NOT NULL
                       THEN (julianday(e.stoppedAt) - julianday(e.startedAt)) * 86400.0
                       ELSE NULL
                   END AS run_time_secs
               FROM execution_entity e
               LEFT JOIN workflow_entity w ON e.workflowId = w.id
               WHERE e.mode = 'trigger'
                 AND e.startedAt IS NOT NULL
                 AND datetime(e.startedAt) BETWEEN datetime('now', '-7 days') AND datetime('now')
               ORDER BY datetime(e.startedAt) DESC, CAST(e.id AS INTEGER) DESC;
               """;
    }
}

internal sealed class N8nExecutionListCacheEntry
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset CachedAt { get; set; }
    public DateTimeOffset? DatabaseLastWriteTimeUtc { get; set; }
    public List<N8nExecutionListRecord> Records { get; set; } = new();
}

internal sealed record N8nExecutionListRecord(
    string ExecutionId,
    string? WorkflowId,
    string WorkflowName,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    double? RunTimeSeconds);

internal sealed record N8nExecutionListSyncResult(
    IReadOnlyList<N8nExecutionListRecord> Records,
    string? ErrorMessage,
    DateTimeOffset? DatabaseLastWriteTimeUtc);
