using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartContainer.EdgeCollector;

internal static partial class SnapshotParser
{
    public static IReadOnlyList<SnapshotRecord> ParseWorksheet(
        string sheetName,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string portName,
        DateTimeOffset collectedAt)
    {
        var header = rows
            .Select((row, index) => new
            {
                Row = row,
                Index = index,
                Carriers = row
                    .Select((value, column) => new CarrierColumn(
                        NormalizeCarrier(value),
                        column))
                    .Where(item => KnownCarrierCodes.Contains(item.Code))
                    .ToList()
            })
            .OrderByDescending(static item => item.Carriers.Count)
            .FirstOrDefault();
        if (header is null || header.Carriers.Count < 5)
        {
            throw new InvalidOperationException(
                $"Worksheet '{sheetName}' does not contain at least five recognized carrier headers.");
        }

        var records = new List<SnapshotRecord>();
        var firstCarrierColumn = header.Carriers.Min(static item => item.Index);
        var currentDate = ParseDate(header.Row, collectedAt);
        var currentYard = FindYard(
            header.Row,
            firstCarrierColumn,
            portName);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowDate = ParseDate(row, collectedAt);
            if (rowDate is not null)
            {
                currentDate = rowDate;
                currentYard = FindYard(row, firstCarrierColumn, currentYard);
            }

            if (rowIndex == header.Index)
            {
                continue;
            }

            var leadingCells = row
                .Take(firstCarrierColumn)
                .Select(static value => NormalizeCell(value))
                .ToList();
            var status = leadingCells.FirstOrDefault(KnownStatuses.Contains);
            var containerType = leadingCells
                .FirstOrDefault(value => ContainerTypeRegex().IsMatch(value))
                ?.ToUpperInvariant();
            if (status is null || containerType is null || currentDate is null)
            {
                continue;
            }

            foreach (var carrier in header.Carriers)
            {
                if (carrier.Index >= row.Count)
                {
                    continue;
                }

                var rawValue = NormalizeCell(row[carrier.Index]);
                if (rawValue.Length == 0)
                {
                    continue;
                }

                if (carrier.Code == "SITC"
                    && rawValue.Equals(
                        "联系sitc",
                        StringComparison.OrdinalIgnoreCase))
                {
                    rawValue = "联系 SITC";
                }

                var numericValue = rawValue.Replace(",", string.Empty);
                int? quantity = int.TryParse(
                    numericValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedQuantity)
                    && parsedQuantity is >= 0 and <= 100000
                    ? parsedQuantity
                    : null;
                records.Add(new SnapshotRecord(
                    sheetName,
                    currentYard,
                    currentDate,
                    status,
                    containerType,
                    carrier.Code,
                    quantity,
                    quantity is null ? rawValue : null,
                    rawValue));
            }
        }

        return records;
    }

    public static void ValidateSnapshot(
        IReadOnlyList<SnapshotRecord> records,
        string expectedPortName)
    {
        if (records.Count < 10)
        {
            throw new InvalidOperationException(
                $"Only {records.Count} valid availability records were parsed.");
        }

        var carrierCount = records
            .Select(static record => record.CarrierCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (carrierCount < 5)
        {
            throw new InvalidOperationException(
                $"Only {carrierCount} recognized carriers were present in parsed records.");
        }

        var dates = records
            .Select(static record => record.DataDate)
            .OfType<DateOnly>()
            .Distinct()
            .ToList();
        if (dates.Count != 1)
        {
            throw new InvalidOperationException(
                "The parsed workbook did not contain exactly one source data date.");
        }

        var expectedKeyword = expectedPortName.Trim().TrimEnd('港');
        if (!records.Any(record => record.Yard.Contains(
                expectedKeyword,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The expected yard keyword '{expectedKeyword}' was not found.");
        }

        var duplicate = records
            .GroupBy(record => new
            {
                record.SheetName,
                record.Yard,
                record.Status,
                record.ContainerType,
                record.CarrierCode
            })
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                "The parsed workbook contained duplicate yard availability cells.");
        }
    }

    private static string FindYard(
        IReadOnlyList<string> row,
        int firstCarrierColumn,
        string fallback)
    {
        var candidate = row
            .Take(firstCarrierColumn)
            .Select(static value => NormalizeCell(value))
            .FirstOrDefault(value =>
                value.Length is > 0 and <= 128
                && !value.Equals("日期", StringComparison.Ordinal)
                && !value.Equals("内河点", StringComparison.Ordinal)
                && !KnownStatuses.Contains(value)
                && !ContainerTypeRegex().IsMatch(value)
                && !DataDateRegex().IsMatch(value)
                && !SlashDateRegex().IsMatch(value));
        return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
    }

    private static DateOnly? ParseDate(
        IReadOnlyList<string> row,
        DateTimeOffset collectedAt)
    {
        var text = string.Join(
            " ",
            row.Where(static value => !string.IsNullOrWhiteSpace(value)));
        var match = DataDateRegex().Match(text);
        if (match.Success
            && int.TryParse(match.Groups["month"].Value, out var month)
            && int.TryParse(match.Groups["day"].Value, out var day))
        {
            return ClosestDate(month, day, collectedAt);
        }

        match = SlashDateRegex().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["month"].Value, out month)
            || !int.TryParse(match.Groups["day"].Value, out day))
        {
            return null;
        }

        if (int.TryParse(match.Groups["year"].Value, out var year)
            && year is >= 2000 and <= 2100
            && DateOnly.TryParseExact(
                $"{year:D4}-{month:D2}-{day:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var explicitDate))
        {
            return explicitDate;
        }

        return ClosestDate(month, day, collectedAt);
    }

    private static DateOnly? ClosestDate(
        int month,
        int day,
        DateTimeOffset collectedAt)
    {
        var localDate = collectedAt.ToOffset(TimeSpan.FromHours(8)).Date;
        return new[] { localDate.Year - 1, localDate.Year, localDate.Year + 1 }
            .Select(year => DateOnly.TryParseExact(
                    $"{year:D4}-{month:D2}-{day:D2}",
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var value)
                ? value
                : (DateOnly?)null)
            .OfType<DateOnly>()
            .OrderBy(value => Math.Abs(
                value.DayNumber - DateOnly.FromDateTime(localDate).DayNumber))
            .Cast<DateOnly?>()
            .FirstOrDefault();
    }

    private static string NormalizeCarrier(string value) =>
        NormalizeCell(value).ToUpperInvariant();

    private static string NormalizeCell(string? value) =>
        Regex.Replace(value?.Trim() ?? string.Empty, "\\s+", " ");

    private sealed record CarrierColumn(string Code, int Index);

    private static readonly HashSet<string> KnownCarrierCodes = new(
        [
            "MSC", "MSK", "CMA", "ESL", "WHL", "YML", "SITC", "KMTC",
            "HLC", "ZIM", "RCL", "HMM", "ONE", "PIL", "NOS", "OOCL"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownStatuses = new(
        ["在场", "在途", "申请中", "实单申请"],
        StringComparer.Ordinal);

    [GeneratedRegex("^(?:20|40|45)(?:GP|HC)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerTypeRegex();

    [GeneratedRegex("(?<month>\\d{1,2})月(?<day>\\d{1,2})日")]
    private static partial Regex DataDateRegex();

    [GeneratedRegex("(?:(?<year>20\\d{2})[/-])?(?<month>\\d{1,2})[/-](?<day>\\d{1,2})")]
    private static partial Regex SlashDateRegex();
}
