using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartContainer.EdgeCollector;

var options = CollectorOptions.FromEnvironment();
Directory.CreateDirectory(options.OutputDirectory);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

try
{
    Console.WriteLine(
        $"Collecting {options.SourceKey} from {options.SourceUrl} with Microsoft Edge.");
    var collector = new WpsEdgeCollector(options);
    var snapshot = await collector.CollectAsync(CancellationToken.None);
    var payloadJson = JsonSerializer.Serialize(snapshot, jsonOptions);
    var payloadPath = Path.Combine(
        options.OutputDirectory,
        "smart-container-snapshot.json");
    await File.WriteAllTextAsync(payloadPath, payloadJson, Encoding.UTF8);
    Console.WriteLine(
        $"Validated {snapshot.Records.Count} records across {snapshot.Records.Select(record => record.Yard).Distinct().Count()} yards.");

    if (options.DryRun)
    {
        Console.WriteLine(
            $"Dry run completed. Snapshot: {payloadPath}");
        return 0;
    }

    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        options.ImportUrl);
    request.Headers.TryAddWithoutValidation(
        "X-Smart-Container-Key",
        options.ImportSecret);
    request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
        "Eshine-SmartContainer-EdgeCollector",
        "1.0"));
    request.Content = new StringContent(
        payloadJson,
        Encoding.UTF8,
        "application/json");
    using var response = await httpClient.SendAsync(request);
    var responseBody = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Import endpoint returned {(int)response.StatusCode}: {responseBody}");
    }

    Console.WriteLine(
        $"Import succeeded with HTTP {(int)response.StatusCode}: {responseBody}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
