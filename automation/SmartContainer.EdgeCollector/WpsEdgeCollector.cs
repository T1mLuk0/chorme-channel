using Microsoft.Playwright;

namespace SmartContainer.EdgeCollector;

public sealed class WpsEdgeCollector(CollectorOptions options)
{
    private const int ViewportWidth = 1440;
    private const int ViewportHeight = 1000;

    public async Task<SnapshotImportRequest> CollectAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var collectedAt = DateTimeOffset.UtcNow;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = ResolveEdgeExecutable(
                    options.EdgeExecutablePath),
                Timeout = options.NavigationTimeoutSeconds * 1000,
                Args =
                [
                    "--disable-dev-shm-usage",
                    "--disable-background-networking",
                    "--disable-component-update"
                ]
            });
        await using var context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                Locale = "zh-CN",
                ViewportSize = new ViewportSize
                {
                    Width = ViewportWidth,
                    Height = ViewportHeight
                }
            });
        await context.GrantPermissionsAsync(
            ["clipboard-read", "clipboard-write"],
            new BrowserContextGrantPermissionsOptions
            {
                Origin = "https://www.kdocs.cn"
            });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(options.NavigationTimeoutSeconds * 1000);
        try
        {
            await page.GotoAsync(
                options.SourceUrl,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = options.NavigationTimeoutSeconds * 1000
                });
            await page.Locator("input.edit-box").WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = options.NavigationTimeoutSeconds * 1000
                });
            await Task.Delay(
                TimeSpan.FromSeconds(options.SettleDelaySeconds),
                cancellationToken);
            await DismissQuickLoginAsync(page);

            var sourceTitle = (await page.TitleAsync()).Trim();
            if (string.IsNullOrWhiteSpace(sourceTitle))
            {
                throw new InvalidOperationException(
                    "The WPS page loaded without a document title.");
            }

            SnapshotImportRequest? snapshot = null;
            Exception? lastValidationException = null;
            var maxAttempts = options.ValidationRetryCount + 1;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var records = new List<SnapshotRecord>();
                foreach (var worksheetName in options.WorksheetNames)
                {
                    var tsv = await CopyWorksheetAsync(
                        page,
                        worksheetName,
                        cancellationToken);
                    var rawPath = Path.Combine(
                        options.OutputDirectory,
                        $"worksheet-{SanitizeFileName(worksheetName)}.tsv");
                    await File.WriteAllTextAsync(rawPath, tsv, cancellationToken);
                    records.AddRange(SnapshotParser.ParseWorksheet(
                        worksheetName,
                        DelimitedText.ParseTsv(tsv),
                        options.PortName,
                        collectedAt));
                }

                try
                {
                    SnapshotParser.ValidateSnapshot(records, options.PortName);
                    snapshot = new SnapshotImportRequest(
                        options.SourceKey,
                        sourceTitle,
                        collectedAt,
                        records);
                    break;
                }
                catch (InvalidOperationException exception)
                    when (attempt < maxAttempts && IsTransientDateValidationFailure(exception))
                {
                    lastValidationException = exception;
                    Console.WriteLine(
                        $"Validation attempt {attempt}/{maxAttempts} found a transient " +
                        $"mixed WPS snapshot: {exception.Message}");
                    Console.WriteLine(
                        $"Waiting {options.ValidationRetryDelaySeconds} seconds before " +
                        "reading all worksheets again.");
                    await Task.Delay(
                        TimeSpan.FromSeconds(options.ValidationRetryDelaySeconds),
                        cancellationToken);
                }
            }

            if (snapshot is null)
            {
                throw lastValidationException
                      ?? new InvalidOperationException(
                          "The WPS snapshot could not be validated.");
            }

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(
                    options.OutputDirectory,
                    "wps-success.png"),
                FullPage = false
            });
            return snapshot;
        }
        catch
        {
            await TryCaptureFailureAsync(page);
            throw;
        }
    }

    private async Task<string> CopyWorksheetAsync(
        IPage page,
        string worksheetName,
        CancellationToken cancellationToken)
    {
        var sheetTab = page.GetByText(
            worksheetName,
            new PageGetByTextOptions { Exact = true });
        if (await sheetTab.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                $"Worksheet tab '{worksheetName}' was not found.");
        }

        await sheetTab.Last.ClickAsync();
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var nameBox = page.Locator("input.edit-box");
        await nameBox.FillAsync("A1:Z200");
        await nameBox.PressAsync("Enter");
        await page.Keyboard.PressAsync("Control+C");
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
        var clipboardText = await page.EvaluateAsync<string>(
            "async () => await navigator.clipboard.readText()");
        if (string.IsNullOrWhiteSpace(clipboardText)
            || !clipboardText.Contains("MSC", StringComparison.OrdinalIgnoreCase)
            || !clipboardText.Contains("SITC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Worksheet '{worksheetName}' did not produce a valid tab-separated clipboard payload.");
        }

        return clipboardText;
    }

    private static bool IsTransientDateValidationFailure(
        InvalidOperationException exception) =>
        exception.Message.Contains(
            "source data date",
            StringComparison.OrdinalIgnoreCase);

    private static async Task DismissQuickLoginAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(300);
        var prompt = page.GetByText(
            "快捷登录并查看",
            new PageGetByTextOptions { Exact = true });
        if (await prompt.CountAsync() == 0 || !await prompt.First.IsVisibleAsync())
        {
            return;
        }

        try
        {
            var container = prompt.First.Locator(
                "xpath=ancestor::*[.//button][1]");
            var buttons = await container.Locator("button").AllAsync();
            var candidates = new List<(ILocator Locator, float Y, float X)>();
            foreach (var button in buttons)
            {
                var box = await button.BoundingBoxAsync();
                if (box is not null && await button.IsVisibleAsync())
                {
                    candidates.Add((button, box.Y, box.X));
                }
            }

            var closeButton = candidates
                .OrderBy(static item => item.Y)
                .ThenByDescending(static item => item.X)
                .FirstOrDefault();
            if (closeButton.Locator is not null)
            {
                await closeButton.Locator.ClickAsync();
                await page.WaitForTimeoutAsync(300);
                return;
            }
        }
        catch (PlaywrightException)
        {
        }

        await page.Mouse.ClickAsync(
            ViewportWidth / 2f + 155,
            ViewportHeight / 2f - 185);
        await page.WaitForTimeoutAsync(300);
    }

    private async Task TryCaptureFailureAsync(IPage page)
    {
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(
                    options.OutputDirectory,
                    "wps-failure.png"),
                FullPage = false
            });
        }
        catch (PlaywrightException)
        {
        }
    }

    private static string ResolveEdgeExecutable(string? configuredPath)
    {
        var candidates = new[]
            {
                configuredPath,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft",
                    "Edge",
                    "Application",
                    "msedge.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft",
                    "Edge",
                    "Application",
                    "msedge.exe")
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException(
                   "Microsoft Edge was not found. Set EDGE_EXECUTABLE_PATH to msedge.exe.");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }
}
