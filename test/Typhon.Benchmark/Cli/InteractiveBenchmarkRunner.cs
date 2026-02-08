using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Spectre.Console;

namespace Typhon.Benchmark.Cli;

/// <summary>
/// Interactive benchmark runner using Spectre.Console for a beautiful CLI experience.
/// </summary>
public class InteractiveBenchmarkRunner
{
    private readonly Assembly _assembly;
    private readonly IReadOnlyList<BenchmarkInfo> _benchmarks;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<BenchmarkInfo>> _categorizedBenchmarks;

    public InteractiveBenchmarkRunner(Assembly assembly)
    {
        _assembly = assembly;
        _benchmarks = BenchmarkDiscovery.DiscoverBenchmarks(assembly);
        _categorizedBenchmarks = BenchmarkDiscovery.GroupByCategory(_benchmarks);
    }

    // Sentinel values for navigation
    private const string BackOption = "[← Back]";
    private static readonly BenchmarkInfo BackBenchmark = new(typeof(object), BackOption, "", -1);

    /// <summary>
    /// Runs the interactive benchmark selection and execution flow with back navigation.
    /// </summary>
    public void Run()
    {
        PrintHeader();

        if (_benchmarks.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No benchmarks found in the assembly.[/]");
            return;
        }

        // Show benchmark summary
        PrintBenchmarkSummary();

        // Navigation loop
        while (true)
        {
            // Step 1: Select category
            var category = SelectCategory();
            if (category == null) return; // Exit requested

            // Step 2: Select benchmark (with back option)
            var benchmark = SelectBenchmark(category);
            if (benchmark == null) return; // Exit requested
            if (benchmark == BackBenchmark) continue; // Go back to category selection

            // Step 3: Configure settings (with back option)
            var (settings, goBack) = ConfigureSettings();
            if (settings == null && !goBack) return; // Exit requested
            if (goBack) continue; // Go back to category selection

            // Step 4: Confirm and run
            if (ConfirmRun(benchmark, settings!))
            {
                RunBenchmark(benchmark, settings!);
                return; // Done
            }
            // If not confirmed, loop back to category selection
        }
    }

    private void PrintHeader()
    {
        AnsiConsole.Clear();

        var titlePanel = new Panel(
            new FigletText("Typhon")
                .Color(Color.Cyan1))
            .BorderColor(Color.Cyan1)
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);

        AnsiConsole.Write(titlePanel);

        AnsiConsole.Write(new Rule("[cyan]Benchmark Runner[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();
    }

    private void PrintBenchmarkSummary()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[yellow]Category[/]").Centered())
            .AddColumn(new TableColumn("[yellow]Benchmarks[/]").Centered())
            .AddColumn(new TableColumn("[yellow]Methods[/]").Centered());

        foreach (var (category, benchmarks) in _categorizedBenchmarks)
        {
            var methodCount = benchmarks.Sum(b => b.MethodCount);
            table.AddRow(
                $"[cyan]{category}[/]",
                $"[white]{benchmarks.Count}[/]",
                $"[grey]{methodCount}[/]"
            );
        }

        table.AddRow(
            "[bold green]Total[/]",
            $"[bold white]{_benchmarks.Count}[/]",
            $"[bold grey]{_benchmarks.Sum(b => b.MethodCount)}[/]"
        );

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private string SelectCategory()
    {
        var categories = _categorizedBenchmarks.Keys.ToList();

        // Add "All Categories" option
        var choices = new List<string> { "[All Categories]" };
        choices.AddRange(categories);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select a benchmark category:[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                .AddChoices(choices)
                .UseConverter(c =>
                {
                    if (c == "[All Categories]")
                        return "[yellow]★[/] All Categories";

                    var count = _categorizedBenchmarks[c].Count;
                    return $"[cyan]●[/] {c} [grey]({count} benchmark{(count != 1 ? "s" : "")})[/]";
                }));

        if (selection == "[All Categories]")
            return "[All]";

        return selection;
    }

    private BenchmarkInfo SelectBenchmark(string category)
    {
        IReadOnlyList<BenchmarkInfo> benchmarksToShow;
        string title;

        if (category == "[All]")
        {
            benchmarksToShow = _benchmarks;
            title = "[green]Select a benchmark to run:[/]";
        }
        else
        {
            benchmarksToShow = _categorizedBenchmarks[category];
            title = $"[green]Select a benchmark from [cyan]{category}[/]:[/]";
        }

        // Add navigation and "Run All" options
        var allOption = new BenchmarkInfo(typeof(object), "[Run All in Selection]", "", 0);
        var choices = new List<BenchmarkInfo> { BackBenchmark, allOption };
        choices.AddRange(benchmarksToShow);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<BenchmarkInfo>()
                .Title(title)
                .PageSize(15)
                .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                .AddChoices(choices)
                .UseConverter(b =>
                {
                    if (b == BackBenchmark)
                        return "[grey]←[/] [dim]Back to categories[/]";

                    if (b == allOption)
                        return "[yellow]★[/] Run All in Selection";

                    var categoryTag = category == "[All]"
                        ? $" [grey dim]({b.Category})[/]"
                        : "";
                    return $"[cyan]●[/] {b.Name}{categoryTag} [grey]({b.MethodCount} method{(b.MethodCount != 1 ? "s" : "")})[/]";
                }));

        if (selection == BackBenchmark)
            return BackBenchmark;

        if (selection == allOption)
        {
            // Create a pseudo-benchmark representing all benchmarks
            return new BenchmarkInfo(
                typeof(object),
                category == "[All]" ? "All Benchmarks" : $"All {category} Benchmarks",
                category == "[All]" ? "[All]" : category,
                benchmarksToShow.Sum(b => b.MethodCount)
            );
        }

        return selection;
    }

    /// <summary>
    /// Configures benchmark settings with back navigation option.
    /// </summary>
    /// <returns>Tuple of (settings, goBack). If goBack is true, user wants to return to selection.</returns>
    private (BenchmarkSettings Settings, bool GoBack) ConfigureSettings()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Configure Settings[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        // Ask if user wants to configure or go back
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Configure benchmark settings:[/]")
                .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                .AddChoices(new[]
                {
                    "Use defaults (warmup: 1, iterations: 1, JSON export)",
                    "Customize settings",
                    BackOption
                })
                .UseConverter(s => s == BackOption
                    ? "[grey]←[/] [dim]Back to benchmark selection[/]"
                    : s == "Use defaults (warmup: 1, iterations: 1, JSON export)"
                        ? "[green]►[/] Use defaults [grey](warmup: 1, iterations: 1, JSON export)[/]"
                        : "[cyan]⚙[/] Customize settings"));

        if (action == BackOption)
            return (null, true);

        var settings = new BenchmarkSettings();

        if (action.StartsWith("Use defaults"))
        {
            // Use default settings
            return (settings, false);
        }

        // Customize settings
        AnsiConsole.WriteLine();

        // Warmup count
        settings.WarmupCount = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Warmup iterations[/] [grey](default: 1)[/]:")
                .DefaultValue(1)
                .ValidationErrorMessage("[red]Please enter a valid number[/]")
                .Validate(n => n >= 0 && n <= 100
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must be between 0 and 100[/]")));

        // Iteration count
        settings.IterationCount = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Benchmark iterations[/] [grey](default: 1)[/]:")
                .DefaultValue(1)
                .ValidationErrorMessage("[red]Please enter a valid number[/]")
                .Validate(n => n >= 1 && n <= 1000
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must be between 1 and 1000[/]")));

        AnsiConsole.WriteLine();

        // Export formats
        var exportChoices = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[green]Select export formats:[/]")
                .NotRequired()
                .PageSize(6)
                .HighlightStyle(new Style(Color.Cyan1))
                .InstructionsText("[grey](Press [cyan]<space>[/] to toggle, [cyan]<enter>[/] to confirm)[/]")
                .AddChoiceGroup("[yellow]Formats[/]", new[]
                {
                    "JSON [grey](always included)[/]",
                    "Markdown",
                    "HTML",
                    "R Plot"
                }));

        settings.ExportJson = true; // Always enabled
        settings.ExportMarkdown = exportChoices.Any(c => c.Contains("Markdown"));
        settings.ExportHtml = exportChoices.Any(c => c.Contains("HTML"));
        settings.ExportRPlot = exportChoices.Any(c => c.Contains("R Plot"));

        // Open in browser option (only if HTML or RPlot selected)
        if (settings.ExportHtml || settings.ExportRPlot)
        {
            AnsiConsole.WriteLine();
            settings.OpenResultsInBrowser = AnsiConsole.Confirm(
                "[cyan]Open results in browser after completion?[/]",
                defaultValue: true);
        }

        return (settings, false);
    }

    private bool ConfirmRun(BenchmarkInfo benchmark, BenchmarkSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Summary[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[yellow]Setting[/]")
            .AddColumn("[yellow]Value[/]");

        summaryTable.AddRow("[cyan]Benchmark[/]", $"[white]{benchmark.Name}[/]");
        summaryTable.AddRow("[cyan]Warmup[/]", $"[white]{settings.WarmupCount} iteration(s)[/]");
        summaryTable.AddRow("[cyan]Iterations[/]", $"[white]{settings.IterationCount}[/]");

        var exports = new List<string>();
        if (settings.ExportJson) exports.Add("[green]JSON[/]");
        if (settings.ExportMarkdown) exports.Add("[blue]Markdown[/]");
        if (settings.ExportHtml) exports.Add("[magenta]HTML[/]");
        if (settings.ExportRPlot) exports.Add("[yellow]R Plot[/]");
        summaryTable.AddRow("[cyan]Exports[/]", string.Join(", ", exports));

        if (settings.OpenResultsInBrowser)
            summaryTable.AddRow("[cyan]Auto-open[/]", "[green]Yes[/]");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm("[green]Run benchmark with these settings?[/]", defaultValue: true);
    }

    private void RunBenchmark(BenchmarkInfo benchmark, BenchmarkSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Running Benchmark[/]").RuleStyle("cyan").LeftJustified());
        AnsiConsole.WriteLine();

        // Build configuration
        var config = BuildConfig(settings);

        // Determine which types to run
        IEnumerable<Type> typesToRun;
        if (benchmark.Type == typeof(object)) // "Run All" was selected
        {
            if (benchmark.Category == "[All]")
            {
                typesToRun = _benchmarks.Select(b => b.Type);
            }
            else
            {
                typesToRun = _categorizedBenchmarks[benchmark.Category].Select(b => b.Type);
            }
        }
        else
        {
            typesToRun = new[] { benchmark.Type };
        }

        // Run the benchmarks
        var summaries = new List<BenchmarkDotNet.Reports.Summary>();
        foreach (var type in typesToRun)
        {
            AnsiConsole.MarkupLine($"[cyan]Running:[/] [white]{type.Name}[/]");
            var summary = BenchmarkRunner.Run(type, config);
            summaries.Add(summary);
        }

        // Open results if requested
        if (settings.OpenResultsInBrowser && summaries.Count > 0)
        {
            OpenResultsInBrowser(summaries.First(), settings);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓[/] Benchmark completed!");

        if (summaries.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Results saved to:[/] [cyan]{summaries.First().ResultsDirectoryPath}[/]");
        }
    }

    private IConfig BuildConfig(BenchmarkSettings settings)
    {
        var config = ManualConfig.CreateMinimumViable()
            .AddJob(Job.Default
                .WithWarmupCount(settings.WarmupCount)
                .WithIterationCount(settings.IterationCount));

        // Add exporters
        config.AddExporter(JsonExporter.Full);

        if (settings.ExportMarkdown)
            config.AddExporter(MarkdownExporter.GitHub);

        if (settings.ExportHtml)
            config.AddExporter(HtmlExporter.Default);

        if (settings.ExportRPlot)
            config.AddExporter(RPlotExporter.Default);

        return config;
    }

    private void OpenResultsInBrowser(BenchmarkDotNet.Reports.Summary summary, BenchmarkSettings settings)
    {
        var resultsDir = summary.ResultsDirectoryPath;
        if (string.IsNullOrEmpty(resultsDir) || !Directory.Exists(resultsDir))
            return;

        string fileToOpen = null;

        // Prefer HTML if available
        if (settings.ExportHtml)
        {
            var htmlFiles = Directory.GetFiles(resultsDir, "*.html");
            fileToOpen = htmlFiles.FirstOrDefault();
        }

        // Fall back to RPlot HTML if available
        if (fileToOpen == null && settings.ExportRPlot)
        {
            // R plots generate multiple files, look for the combined one or any HTML
            var rplotFiles = Directory.GetFiles(resultsDir, "*.html");
            fileToOpen = rplotFiles.FirstOrDefault();
        }

        if (fileToOpen != null)
        {
            AnsiConsole.MarkupLine($"[cyan]Opening results in browser...[/]");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fileToOpen,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Could not open browser:[/] [grey]{ex.Message}[/]");
            }
        }
    }
}
