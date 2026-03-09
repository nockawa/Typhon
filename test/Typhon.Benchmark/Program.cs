using BenchmarkDotNet.Running;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Typhon.Benchmark.Cli;

namespace Typhon.Benchmark;

class Program
{
    /// <summary>
    /// Combines multiple BenchmarkDotNet JSON result files into a single file.
    /// Useful for aggregating results from multiple benchmark runs.
    /// </summary>
    private static void CombineBenchmarkResults(
        string resultsDir = "./BenchmarkDotNet.Artifacts/results",
        string resultsFile = "Combined.Benchmarks",
        string searchPattern = "*full.json")
    {
        var resultsPath = Path.Combine(resultsDir, resultsFile + ".json");

        if (!Directory.Exists(resultsDir))
            throw new DirectoryNotFoundException($"Directory not found '{resultsDir}'");

        if (File.Exists(resultsPath))
            File.Delete(resultsPath);

        var reports = Directory
            .GetFiles(resultsDir, searchPattern, SearchOption.TopDirectoryOnly)
            .ToArray();

        if (reports.Length == 0)
            throw new FileNotFoundException($"Reports not found '{searchPattern}'");

        var combinedReport = JsonNode.Parse(File.ReadAllText(reports.First()))!;
        var title = combinedReport["Title"]!;
        var benchmarks = combinedReport["Benchmarks"]!.AsArray();

        // Rename title whilst keeping original timestamp
        combinedReport["Title"] = $"{resultsFile}{title.GetValue<string>()[^16..]}";

        foreach (var report in reports.Skip(1))
        {
            var array = JsonNode.Parse(File.ReadAllText(report))!["Benchmarks"]!.AsArray();
            foreach (var benchmark in array)
            {
                // Double parse avoids "The node already has a parent" exception
                benchmarks.Add(JsonNode.Parse(benchmark!.ToJsonString())!);
            }
        }

        File.WriteAllText(resultsPath, combinedReport.ToString());
    }

    static void Main(string[] args)
    {
        // If command-line arguments are provided, use BenchmarkDotNet's built-in handling
        // This preserves backward compatibility with --filter, --list, etc.
        if (args.Length > 0)
        {
            // Handle special commands
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
            {
                PrintHelp();
                return;
            }

            if ((args.Contains("--list") || args.Contains("-l")) && !args.Contains("--allCategories") && !args.Contains("--anyCategories"))
            {
                ListBenchmarks();
                return;
            }

            if (args.Contains("--profile-delete"))
            {
                BTreeDeleteProfile.Run();
                return;
            }

            if (args.Contains("--profile-scan"))
            {
                BTreeScanProfile.Run();
                return;
            }

            // BTree profile shortcuts: --btree-fast, --btree-medium, --btree-full
            // These run curated subsets of BTree benchmarks for quick/medium/full analysis.
            if (args.Contains("--btree-fast"))
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(["--allCategories", "BTreeFast", "--exporters", "json"]);
                return;
            }

            if (args.Contains("--btree-medium"))
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(["--allCategories", "BTreeMedium", "--exporters", "json"]);
                return;
            }

            if (args.Contains("--btree-full"))
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(["--anyCategories", "BTreeFast", "BTreeMedium", "BTreeFull", "--exporters", "json"]);
                return;
            }

            // Pass through to BenchmarkDotNet for standard args like --filter
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return;
        }

        // No arguments: launch interactive mode
        try
        {
            var runner = new InteractiveBenchmarkRunner(typeof(Program).Assembly);
            runner.Run();
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
    }

    private static void PrintHelp()
    {
        AnsiConsole.Write(new Rule("[cyan]Typhon Benchmark Runner[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[yellow]Option[/]")
            .AddColumn("[yellow]Description[/]");

        table.AddRow("[cyan](no args)[/]", "Launch interactive mode with menu selection");
        table.AddRow("[cyan]--list, -l[/]", "List all available benchmarks");
        table.AddRow("[cyan]--filter <pattern>[/]", "Run benchmarks matching the pattern (e.g., --filter *BTree*)");
        table.AddRow("[cyan]--btree-fast[/]", "BTree quick profile: core ops + 2 concurrent (~3 min)");
        table.AddRow("[cyan]--btree-medium[/]", "BTree medium profile: all key types + concurrent scaling (~15 min)");
        table.AddRow("[cyan]--btree-full[/]", "BTree full profile: everything including tree sizes + enumeration (~50 min)");
        table.AddRow("[cyan]--help, -h, -?[/]", "Show this help message");
        table.AddRow("[cyan]--exporters <list>[/]", "Export formats: json,markdown,html,rplot");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[grey]Examples:[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run[/]                           [grey]# Interactive mode[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run -- --filter *BTree*[/]       [grey]# Run BTree benchmarks[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run -- --btree-fast[/]           [grey]# Quick BTree baseline (~3 min)[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run -- --list[/]                 [grey]# List all benchmarks[/]");
        AnsiConsole.WriteLine();
    }

    private static void ListBenchmarks()
    {
        var benchmarks = BenchmarkDiscovery.DiscoverBenchmarks(typeof(Program).Assembly);
        var grouped = BenchmarkDiscovery.GroupByCategory(benchmarks);

        AnsiConsole.Write(new Rule("[cyan]Available Benchmarks[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        foreach (var (category, categoryBenchmarks) in grouped)
        {
            AnsiConsole.MarkupLine($"[yellow]■[/] [bold]{category}[/]");

            foreach (var benchmark in categoryBenchmarks)
            {
                AnsiConsole.MarkupLine($"    [cyan]●[/] {benchmark.Name} [grey]({benchmark.MethodCount} method{(benchmark.MethodCount != 1 ? "s" : "")})[/]");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[grey]Total:[/] [white]{benchmarks.Count}[/] benchmarks, [white]{benchmarks.Sum(b => b.MethodCount)}[/] methods");
        AnsiConsole.WriteLine();
    }
}