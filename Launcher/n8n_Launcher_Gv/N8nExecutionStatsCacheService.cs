using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace n8n_launcher_Gv;

internal sealed class N8nExecutionStatsCacheService
{
    private const int CurrentSchemaVersion = 1;
    private const string CacheFileName = "execution_stats_cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<N8nExecutionStatsCacheEntry?> ReadAsync(string stateDir, CancellationToken cancellationToken = default)
    {
        string cachePath = GetCachePath(stateDir);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var entry = await JsonSerializer.DeserializeAsync<N8nExecutionStatsCacheEntry>(stream, JsonOptions, cancellationToken);
            if (entry?.SchemaVersion != CurrentSchemaVersion || entry.Stats is null)
            {
                Debug.WriteLine($"[ExecutionStatsCache] Ignore incompatible cache: {cachePath}");
                return null;
            }

            return entry;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionStatsCache] Read failed: {ex.Message}");
            return null;
        }
    }

    public async Task WriteAsync(
        string stateDir,
        N8nExecutionStats stats,
        DateTimeOffset? databaseLastWriteTimeUtc,
        CancellationToken cancellationToken = default)
    {
        string cachePath = GetCachePath(stateDir);
        string? cacheDir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        var entry = new N8nExecutionStatsCacheEntry
        {
            SchemaVersion = CurrentSchemaVersion,
            CachedAt = DateTimeOffset.Now,
            DatabaseLastWriteTimeUtc = databaseLastWriteTimeUtc,
            Stats = stats with { ErrorMessage = null }
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
            Debug.WriteLine($"[ExecutionStatsCache] Write failed: {ex.Message}");

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                Debug.WriteLine($"[ExecutionStatsCache] Temp cleanup failed: {cleanupEx.Message}");
            }
        }
    }

    public static string GetCachePath(string stateDir)
    {
        return Path.Combine(stateDir, CacheFileName);
    }
}

internal sealed class N8nExecutionStatsCacheEntry
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset CachedAt { get; set; }
    public DateTimeOffset? DatabaseLastWriteTimeUtc { get; set; }
    public N8nExecutionStats? Stats { get; set; }
}
