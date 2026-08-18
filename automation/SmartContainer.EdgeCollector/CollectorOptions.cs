namespace SmartContainer.EdgeCollector;

public sealed record CollectorOptions(
    string SourceKey,
    string PortName,
    string SourceUrl,
    string[] WorksheetNames,
    string ImportUrl,
    string ImportSecret,
    string OutputDirectory,
    string? EdgeExecutablePath,
    bool DryRun,
    int NavigationTimeoutSeconds,
    int SettleDelaySeconds)
{
    public static CollectorOptions FromEnvironment()
    {
        var dryRun = GetBoolean("SMART_CONTAINER_DRY_RUN", false);
        var importUrl = Get("SMART_CONTAINER_IMPORT_URL");
        var importSecret = Get("SMART_CONTAINER_IMPORT_SECRET");
        if (!dryRun && (string.IsNullOrWhiteSpace(importUrl)
                        || string.IsNullOrWhiteSpace(importSecret)))
        {
            throw new InvalidOperationException(
                "SMART_CONTAINER_IMPORT_URL and SMART_CONTAINER_IMPORT_SECRET are required unless SMART_CONTAINER_DRY_RUN=true.");
        }

        return new CollectorOptions(
            Get("SMART_CONTAINER_SOURCE_KEY", "taicang"),
            Get("SMART_CONTAINER_PORT_NAME", "太仓港"),
            Get(
                "SMART_CONTAINER_SOURCE_URL",
                "https://www.kdocs.cn/l/cnTbqZoPh8FG"),
            Get("SMART_CONTAINER_WORKSHEETS", "太仓,内河点")
                .Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries),
            importUrl,
            importSecret,
            Path.GetFullPath(Get(
                "SMART_CONTAINER_OUTPUT_DIR",
                Path.Combine("artifacts", "smart-container-run"))),
            NullIfEmpty(Get("EDGE_EXECUTABLE_PATH")),
            dryRun,
            GetInteger("SMART_CONTAINER_NAVIGATION_TIMEOUT_SECONDS", 90, 30, 180),
            GetInteger("SMART_CONTAINER_SETTLE_DELAY_SECONDS", 12, 5, 60));
    }

    private static string Get(string name, string fallback = "") =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : fallback;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool GetBoolean(string name, bool fallback) =>
        bool.TryParse(Get(name), out var value) ? value : fallback;

    private static int GetInteger(
        string name,
        int fallback,
        int minimum,
        int maximum) =>
        int.TryParse(Get(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}
