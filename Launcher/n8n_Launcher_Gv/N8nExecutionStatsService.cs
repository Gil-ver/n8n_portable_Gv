using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace n8n_launcher_Gv;

internal sealed class N8nExecutionStatsService
{
    public async Task<N8nExecutionStats> ReadLast7DaysAsync(string portableRoot, CancellationToken cancellationToken = default)
    {
        try
        {
            string databasePath = Path.Combine(portableRoot, "data", ".n8n", "database.sqlite");
            if (!File.Exists(databasePath))
            {
                return N8nExecutionStats.Empty($"SQLite database not found: {databasePath}");
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

            // 与 n8n 官方 Insights 摘要一致：滚动 7 天窗口 = [now - 7d, now]，UTC 时刻精确到秒。
            // 不做日界对齐（官方仅在用户自定义 endDate 不是今天时才对齐日界；默认窗口保留时分秒）。
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset fromUtc = nowUtc.AddDays(-7);
            string fromUtcIso = fromUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string toUtcIso = nowUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            // daily 折线仍按本地日界渲染（FillMissingLast7Days 需要本地今日锚点）。
            DateTime todayLocalMidnight = DateTime.Today;

            // 优先走 insights_by_period（企业版/insights 开启时）
            try
            {
                var overview = await TryQueryInsightsOverviewAsync(connection, fromUtcIso, toUtcIso, cancellationToken);
                if (overview.HasValue)
                {
                    var daily = await TryQueryInsightsDailyAsync(connection, fromUtcIso, toUtcIso, cancellationToken);
                    return new N8nExecutionStats(
                        overview.Value.TotalCount,
                        overview.Value.FailedCount,
                        overview.Value.FailureRatePercent,
                        overview.Value.AverageRuntimeSeconds,
                        FillMissingLast7Days(daily ?? (IReadOnlyList<N8nExecutionStatsDailyPoint>)Array.Empty<N8nExecutionStatsDailyPoint>(), todayLocalMidnight));
                }
            }
            catch (SqliteException ex) when (IsMissingInsightsTable(ex))
            {
                Debug.WriteLine($"[ExecutionStats] insights_by_period unavailable, fallback to execution_entity: {ex.Message}");
            }

            // fallback: execution_entity
            OverviewValues fallbackOverview;
            IReadOnlyList<N8nExecutionStatsDailyPoint> fallbackDaily;
            try
            {
                fallbackOverview = await QueryExecutionEntityOverviewAsync(connection, useStatusColumn: true, fromUtcIso, toUtcIso, cancellationToken);
                fallbackDaily = await QueryExecutionEntityDailyAsync(connection, useStatusColumn: true, fromUtcIso, toUtcIso, cancellationToken);
            }
            catch (SqliteException ex) when (IsMissingStatusColumn(ex))
            {
                Debug.WriteLine($"[ExecutionStats] status column unavailable, fallback to finished column: {ex.Message}");
                fallbackOverview = await QueryExecutionEntityOverviewAsync(connection, useStatusColumn: false, fromUtcIso, toUtcIso, cancellationToken);
                fallbackDaily = await QueryExecutionEntityDailyAsync(connection, useStatusColumn: false, fromUtcIso, toUtcIso, cancellationToken);
            }

            return new N8nExecutionStats(
                fallbackOverview.TotalCount,
                fallbackOverview.FailedCount,
                fallbackOverview.FailureRatePercent,
                fallbackOverview.AverageRuntimeSeconds,
                FillMissingLast7Days(fallbackDaily, todayLocalMidnight));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionStats] Read failed: {ex.Message}");
            return N8nExecutionStats.Empty(ex.Message);
        }
    }

    public static DateTimeOffset? GetDatabaseLastWriteTimeUtc(string portableRoot)
    {
        string databasePath = Path.Combine(portableRoot, "data", ".n8n", "database.sqlite");
        string walPath = databasePath + "-wal";
        DateTimeOffset? a = File.Exists(databasePath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(databasePath), TimeSpan.Zero) : null;
        DateTimeOffset? b = File.Exists(walPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(walPath), TimeSpan.Zero) : null;
        if (a is null) return b;
        if (b is null) return a;
        return b > a ? b : a;
    }

    private readonly record struct OverviewValues(int TotalCount, int FailedCount, double FailureRatePercent, double? AverageRuntimeSeconds);

    /// <summary>
    /// insights_by_period overview：最近 7 天窗口，累计 type=2/3 的 value。
    /// 与网页 Insights 顶部数字口径一致。
    /// </summary>
    private static async Task<OverviewValues?> TryQueryInsightsOverviewAsync(SqliteConnection connection, string fromUtcIso, string toUtcIso, CancellationToken cancellationToken)
    {
        const string sql = "SELECT " +
                           "COALESCE(SUM(CASE WHEN type IN (2, 3) THEN value ELSE 0 END), 0) AS total_count, " +
                           "COALESCE(SUM(CASE WHEN type = 3 THEN value ELSE 0 END), 0) AS failed_count, " +
                           "COALESCE(SUM(CASE WHEN type = 1 THEN value ELSE 0 END), 0) AS total_runtime_ms " +
                           "FROM insights_by_period " +
                           "WHERE periodUnit = 0 " +
                           "AND datetime(periodStart) >= datetime($from) " +
                           "AND datetime(periodStart) <  datetime($to);";
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromUtcIso);
        command.Parameters.AddWithValue("$to", toUtcIso);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new OverviewValues(0, 0, 0, null);
        int totalCount = ReadInt(reader, "total_count");
        int failedCount = ReadInt(reader, "failed_count");
        double totalRuntimeMs = ReadDouble(reader, "total_runtime_ms");
        // 与 n8n 官方 getInsightsSummary 对齐：保留 0~1 小数（不是百分比），round 到 3 位小数。
        double failureRatePercent = totalCount > 0 ? Math.Round((double)failedCount / totalCount, 3, MidpointRounding.AwayFromZero) : 0;
        // 与 n8n 官方对齐：秒级，保留 2 位小数。
        double? avgRuntimeSeconds = totalCount > 0 ? Math.Round(totalRuntimeMs / totalCount / 1000.0, 2, MidpointRounding.AwayFromZero) : null;
        return new OverviewValues(totalCount, failedCount, failureRatePercent, avgRuntimeSeconds);
    }

    /// <summary>
    /// execution_entity overview：最近 7 天窗口（按 startedAt 过滤）。
    /// 与网页 Insights 顶部数字口径一致。
    /// </summary>
    private static async Task<OverviewValues> QueryExecutionEntityOverviewAsync(SqliteConnection connection, bool useStatusColumn, string fromUtcIso, string toUtcIso, CancellationToken cancellationToken)
    {
        string failedExpr = useStatusColumn
            ? "CASE WHEN status IN ('failed', 'error', 'crashed') THEN 1 ELSE 0 END"
            : "CASE WHEN COALESCE(finished, 0) = 0 THEN 1 ELSE 0 END";
        string sql = "SELECT " +
                     "COUNT(*) AS total_count, " +
                     "SUM(" + failedExpr + ") AS failed_count, " +
                     "AVG(CASE WHEN stoppedAt IS NOT NULL AND startedAt IS NOT NULL THEN (julianday(stoppedAt) - julianday(startedAt)) * 86400.0 ELSE NULL END) AS avg_runtime_seconds " +
                     "FROM execution_entity " +
                     "WHERE datetime(startedAt) >= datetime($from) " +
                     "AND datetime(startedAt) <  datetime($to);";
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromUtcIso);
        command.Parameters.AddWithValue("$to", toUtcIso);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new OverviewValues(0, 0, 0, null);
        int totalCount = ReadInt(reader, "total_count");
        int failedCount = ReadInt(reader, "failed_count");
        int avgOrdinal = reader.GetOrdinal("avg_runtime_seconds");
        // 与 n8n 官方对齐：秒级，保留 2 位小数。
        double? avgRuntimeSeconds = reader.IsDBNull(avgOrdinal) ? null : Math.Round(Convert.ToDouble(reader[avgOrdinal], CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero);
        // 与 n8n 官方 getInsightsSummary 对齐：保留 0~1 小数（不是百分比），round 到 3 位小数。
        double failureRatePercent = totalCount > 0 ? Math.Round((double)failedCount / totalCount, 3, MidpointRounding.AwayFromZero) : 0;
        return new OverviewValues(totalCount, failedCount, failureRatePercent, avgRuntimeSeconds);
    }

    /// <summary>
    /// insights_by_period 的 daily 折线数据。按本地日期分组（'localtime' modifier），
    /// 让最右一格 = 本地今天，而不是 UTC 今天（否则东八区会错位一天）。
    /// </summary>
    private static async Task<IReadOnlyList<N8nExecutionStatsDailyPoint>?> TryQueryInsightsDailyAsync(SqliteConnection connection, string fromUtcIso, string toUtcIso, CancellationToken cancellationToken)
    {
        const string sql = "SELECT date(periodStart) AS day, " +
                           "COALESCE(SUM(CASE WHEN type IN (2, 3) THEN value ELSE 0 END), 0) AS total_count, " +
                           "COALESCE(SUM(CASE WHEN type = 3 THEN value ELSE 0 END), 0) AS failed_count, " +
                           "COALESCE(SUM(CASE WHEN type = 1 THEN value ELSE 0 END), 0) AS total_runtime_ms " +
                           "FROM insights_by_period " +
                           "WHERE periodUnit = 0 " +
                           "AND datetime(periodStart) >= datetime($from) " +
                           "AND datetime(periodStart) <  datetime($to) " +
                           "GROUP BY day ORDER BY day;";
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromUtcIso);
        command.Parameters.AddWithValue("$to", toUtcIso);
        var rows = new List<N8nExecutionStatsDailyPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string dayText = Convert.ToString(reader["day"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)) continue;
            int totalCount = ReadInt(reader, "total_count");
            int failedCount = ReadInt(reader, "failed_count");
            double totalRuntimeMs = ReadDouble(reader, "total_runtime_ms");
            double failureRatePercent = totalCount > 0 ? failedCount * 100.0 / totalCount : 0;
            double? averageRuntimeSeconds = totalCount > 0 ? totalRuntimeMs / totalCount / 1000.0 : null;
            rows.Add(new N8nExecutionStatsDailyPoint(day, totalCount, failedCount, failureRatePercent, averageRuntimeSeconds));
        }
        return rows;
    }

    /// <summary>
    /// execution_entity 的 daily 折线数据。按本地日期分组。
    /// </summary>
    private static async Task<IReadOnlyList<N8nExecutionStatsDailyPoint>> QueryExecutionEntityDailyAsync(SqliteConnection connection, bool useStatusColumn, string fromUtcIso, string toUtcIso, CancellationToken cancellationToken)
    {
        string failedExpr = useStatusColumn
            ? "CASE WHEN status IN ('failed', 'error', 'crashed') THEN 1 ELSE 0 END"
            : "CASE WHEN COALESCE(finished, 0) = 0 THEN 1 ELSE 0 END";
        string sql = "SELECT date(startedAt) AS day, " +
                     "COUNT(*) AS total_count, " +
                     "SUM(" + failedExpr + ") AS failed_count, " +
                     "AVG(CASE WHEN stoppedAt IS NOT NULL AND startedAt IS NOT NULL THEN (julianday(stoppedAt) - julianday(startedAt)) * 86400.0 ELSE NULL END) AS avg_runtime_seconds " +
                     "FROM execution_entity " +
                     "WHERE datetime(startedAt) >= datetime($from) " +
                     "AND datetime(startedAt) <  datetime($to) " +
                     "GROUP BY day ORDER BY day;";
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromUtcIso);
        command.Parameters.AddWithValue("$to", toUtcIso);
        var rows = new List<N8nExecutionStatsDailyPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string dayText = Convert.ToString(reader["day"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)) continue;
            int totalCount = ReadInt(reader, "total_count");
            int failedCount = ReadInt(reader, "failed_count");
            int avgOrdinal = reader.GetOrdinal("avg_runtime_seconds");
            double? averageRuntimeSeconds = reader.IsDBNull(avgOrdinal) ? null : Convert.ToDouble(reader[avgOrdinal], CultureInfo.InvariantCulture);
            double failureRatePercent = totalCount > 0 ? failedCount * 100.0 / totalCount : 0;
            rows.Add(new N8nExecutionStatsDailyPoint(day, totalCount, failedCount, failureRatePercent, averageRuntimeSeconds));
        }
        return rows;
    }

    /// <summary>
    /// 把 daily 结果补齐成完整最近 7 天，缺失天填 0。锚点 = 本地今天。
    /// </summary>
    private static IReadOnlyList<N8nExecutionStatsDailyPoint> FillMissingLast7Days(IReadOnlyList<N8nExecutionStatsDailyPoint> rows, DateTime todayLocalMidnight)
    {
        var byDate = new Dictionary<DateOnly, N8nExecutionStatsDailyPoint>();
        foreach (var r in rows) byDate[r.Date] = r;
        DateOnly today = DateOnly.FromDateTime(todayLocalMidnight);
        var result = new List<N8nExecutionStatsDailyPoint>(7);
        for (int i = 6; i >= 0; i--)
        {
            DateOnly date = today.AddDays(-i);
            result.Add(byDate.TryGetValue(date, out var point)
                ? point
                : new N8nExecutionStatsDailyPoint(date, 0, 0, 0, null));
        }
        return result;
    }

    private static bool IsMissingInsightsTable(SqliteException ex)
    {
        return ex.SqliteErrorCode == 1 && ex.Message.Contains("insights_by_period", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingStatusColumn(SqliteException ex)
    {
        return ex.SqliteErrorCode == 1 && ex.Message.Contains("status", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader[ordinal], CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader[ordinal], CultureInfo.InvariantCulture);
    }
}
