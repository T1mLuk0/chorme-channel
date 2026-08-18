namespace SmartContainer.EdgeCollector;

public sealed record SnapshotImportRequest(
    string SourceKey,
    string SourceTitle,
    DateTimeOffset CollectedAt,
    IReadOnlyList<SnapshotRecord> Records);

public sealed record SnapshotRecord(
    string SheetName,
    string Yard,
    DateOnly? DataDate,
    string Status,
    string ContainerType,
    string CarrierCode,
    int? Quantity,
    string? AvailabilityText,
    string RawValue);
