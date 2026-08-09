namespace n8n_launcher_Gv;

internal sealed record N8nExecutionStatsDailyPoint(
    DateOnly Date,
    int TotalCount,
    int FailedCount,
    double FailureRatePercent,
    double? AverageRuntimeSeconds);

internal sealed record N8nExecutionStats(
    int TotalCount,
    int FailedCount,
    double FailureRatePercent,
    double? AverageRuntimeSeconds,
    IReadOnlyList<N8nExecutionStatsDailyPoint>? DailyPoints = null,
    string? ErrorMessage = null)
{
    public static N8nExecutionStats Empty(string? errorMessage = null) => new(0, 0, 0, null, Array.Empty<N8nExecutionStatsDailyPoint>(), errorMessage);
}
