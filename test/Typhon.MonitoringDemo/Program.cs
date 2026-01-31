using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using Spectre.Console;
using System.Diagnostics;
using Typhon.Engine;
using Typhon.MonitoringDemo;
using Typhon.MonitoringDemo.Scenarios;

namespace Typhon.MonitoringDemo;

// ============================================================================
// Typhon Monitoring Demo
// ============================================================================
// This application bootstraps an Aspire Dashboard, connects Typhon's OTel
// metrics to it, and lets you run various load scenarios to observe the
// database engine behavior in real-time.
// ============================================================================

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("Typhon Monitor").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]Real-time observability for the Typhon database engine[/]");
        AnsiConsole.WriteLine();

        // Ensure a fresh database for testing (delete any existing file from previous runs)
        var dbOptions = new ManagedPagedMMFOptions { DatabaseName = "TyphonMonitoringDemo" };
        dbOptions.EnsureFileDeleted();

        // Start the Aspire Dashboard container
        var aspireLauncher = new AspireDashboardLauncher();
        var dashboardUrl = await aspireLauncher.StartAsync();

        if (dashboardUrl == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to start Aspire Dashboard. Exiting.[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Dashboard Ready[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[bold cyan]Aspire Dashboard:[/] [link={dashboardUrl}]{dashboardUrl}[/]");
        AnsiConsole.MarkupLine("[grey]Open this URL in your browser to view metrics[/]");
        AnsiConsole.WriteLine();

        // Initialize Typhon with OpenTelemetry
        using var host = CreateHost(aspireLauncher.OtlpEndpoint);

        // Start the host to activate OpenTelemetry MeterProvider and other hosted services
        await host.StartAsync();

        var typhonContext = host.Services.GetRequiredService<TyphonContext>();

        // Initialize the database
        typhonContext.Initialize();

        // Get available scenario factories
        var scenarioFactories = ScenarioRegistry.GetScenarioFactories();

        // Main menu loop
        var running = true;
        while (running)
        {
            AnsiConsole.Write(new Rule("[yellow]Select a Scenario[/]").RuleStyle("grey"));

            var choices = scenarioFactories.Select(s => s.Name).Append("Exit").ToArray();

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to simulate?")
                    .PageSize(10)
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(choices));

            if (selection == "Exit")
            {
                running = false;
                continue;
            }

            var scenarioInfo = scenarioFactories.First(s => s.Name == selection);

            // Configure scenario parameters
            var config = ConfigureScenario(scenarioInfo.Name, scenarioInfo.Description);
            if (config == null)
            {
                continue; // User cancelled
            }

            // Create a fresh scenario instance (avoids stale entity ID state from previous runs)
            var scenario = scenarioInfo.Factory();

            // Run the scenario
            await RunScenarioAsync(scenario, config, typhonContext);
        }

        // Cleanup
        AnsiConsole.MarkupLine("[grey]Shutting down...[/]");
        typhonContext.Dispose();
        await host.StopAsync();
        await aspireLauncher.StopAsync();
        AnsiConsole.MarkupLine("[green]Goodbye![/]");

        return 0;
    }

    private static IHost CreateHost(string otlpEndpoint)
    {
        var builder = Host.CreateApplicationBuilder();

        // Disable console logging to keep Spectre.Console output clean
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Configure Typhon
        TelemetryConfig.EnsureInitialized();

        // Register IResourceRegistry and IMemoryAllocator via proper DI
        builder.Services.AddResourceRegistry();
        builder.Services.AddMemoryAllocator();
        builder.Services.AddSingleton<IResourceGraph>(sp => new ResourceGraph(sp.GetRequiredService<IResourceRegistry>()));

        // Register Typhon services as SINGLETONS - we want the same engine for the entire app lifetime
        builder.Services
            .AddManagedPagedMMF(options =>
            {
                options.DatabaseName = "TyphonMonitoringDemo";
                options.DatabaseCacheSize = 16 * 1024 * 1024; // 16MB cache for stress testing
                options.PagesDebugPattern = false;
            })
            .AddDatabaseEngine(_ => { });

        // Register observability bridge (uses the IResourceGraph we registered above)
        builder.Services.AddTyphonObservabilityBridge(options =>
        {
            options.SnapshotInterval = TimeSpan.FromSeconds(1);
        });

        // Configure OpenTelemetry to send to Aspire Dashboard
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ResourceMetricsExporter.MeterName)
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                        options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                    });
            });

        // Register TyphonContext as singleton - same engine for all scenarios
        builder.Services.AddSingleton<TyphonContext>();

        return builder.Build();
    }

    private static ScenarioConfig ConfigureScenario(string name, string description)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[cyan]{name}[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[grey]{description}[/]");
        AnsiConsole.WriteLine();

        // Duration selection
        var duration = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How long should the simulation run?")
                .AddChoices("10 seconds", "30 seconds", "1 minute", "5 minutes", "Cancel"));

        if (duration == "Cancel")
        {
            return null;
        }

        var durationSeconds = duration switch
        {
            "10 seconds" => 10,
            "30 seconds" => 30,
            "1 minute" => 60,
            "5 minutes" => 300,
            _ => 30
        };

        // Load intensity selection
        var intensity = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select load intensity:")
                .AddChoices("Light (100 ops/s)", "Medium (1,000 ops/s)", "Heavy (10,000 ops/s)", "Stress (max throughput)"));

        var opsPerSecond = intensity switch
        {
            "Light (100 ops/s)" => 100,
            "Medium (1,000 ops/s)" => 1000,
            "Heavy (10,000 ops/s)" => 10000,
            "Stress (max throughput)" => int.MaxValue,
            _ => 1000
        };

        // Concurrency selection
        var concurrency = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How many concurrent workers?")
                .AddChoices("1 (sequential)", "4 workers", "8 workers", "16 workers"));

        var workerCount = concurrency switch
        {
            "1 (sequential)" => 1,
            "4 workers" => 4,
            "8 workers" => 8,
            "16 workers" => 16,
            _ => 4
        };

        return new ScenarioConfig
        {
            DurationSeconds = durationSeconds,
            TargetOpsPerSecond = opsPerSecond,
            WorkerCount = workerCount
        };
    }

    private static async Task RunScenarioAsync(IScenario scenario, ScenarioConfig config, TyphonContext typhonContext)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold green]Starting:[/] {scenario.Name}");
        AnsiConsole.MarkupLine($"[grey]Duration: {config.DurationSeconds}s | Target: {config.TargetOpsPerSecond} ops/s | Workers: {config.WorkerCount}[/]");
        AnsiConsole.WriteLine();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.DurationSeconds));
        var stats = new ScenarioStats();
        var startTime = Stopwatch.GetTimestamp();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            ])
            .StartAsync(async ctx =>
            {
                var progressTask = ctx.AddTask($"[cyan]{scenario.Name}[/]", maxValue: config.DurationSeconds);

                // Update progress in background
                var progressUpdate = Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                        progressTask.Value = Math.Min(elapsed, config.DurationSeconds);
                        await Task.Delay(100, CancellationToken.None);
                    }
                    progressTask.Value = config.DurationSeconds;
                });

                // Run the scenario
                try
                {
                    await scenario.RunAsync(typhonContext, config, stats, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected when duration expires
                }

                await progressUpdate;
            });

        // Show results
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Results[/]").RuleStyle("grey"));

        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn("Value", c => c.RightAligned());

        table.AddRow("Total Operations", stats.TotalOperations.ToString("N0"));
        table.AddRow("Successful", $"[green]{stats.SuccessfulOperations:N0}[/]");
        table.AddRow("Failed", stats.FailedOperations > 0 ? $"[red]{stats.FailedOperations:N0}[/]" : "0");
        table.AddRow("Transactions Committed", stats.TransactionsCommitted.ToString("N0"));
        table.AddRow("Transactions Rolled Back", stats.TransactionsRolledBack.ToString("N0"));
        table.AddRow("Avg Ops/Second", $"{stats.TotalOperations / (double)config.DurationSeconds:N0}");

        if (stats.AverageLatencyUs > 0)
        {
            table.AddRow("Avg Latency", $"{stats.AverageLatencyUs:N1} µs");
        }

        AnsiConsole.Write(table);

        // Show the first exception if there were failures
        if (stats.FirstException != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[red]First Exception[/]").RuleStyle("grey"));
            AnsiConsole.MarkupLine($"[red]{stats.FirstException.GetType().Name}:[/] {Markup.Escape(stats.FirstException.Message)}");
            if (stats.FirstException.StackTrace != null)
            {
                var shortStack = string.Join("\n", stats.FirstException.StackTrace.Split('\n').Take(5));
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(shortStack)}[/]");
            }
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Check the Aspire Dashboard for detailed metrics![/]");
        AnsiConsole.WriteLine();

        // Wait for user before returning to menu
        AnsiConsole.MarkupLine("[dim]Press any key to return to scenario selection...[/]");
        Console.ReadKey(true);
        AnsiConsole.WriteLine();
    }
}
