using Spectre.Console;
using System.Diagnostics;
using System.Net.Sockets;

namespace Typhon.MonitoringDemo;

/// <summary>
/// Launches and manages the Aspire Dashboard container for metrics visualization.
/// </summary>
public sealed class AspireDashboardLauncher
{
    private const string ContainerName = "typhon-aspire-dashboard";
    private const string ImageName = "mcr.microsoft.com/dotnet/aspire-dashboard:9.0";
    private const int DashboardPort = 18888;
    private const int OtlpGrpcPort = 4317;

    private Process _containerProcess;

    /// <summary>
    /// Gets the dashboard URL once started.
    /// </summary>
    public string DashboardUrl { get; private set; }

    /// <summary>
    /// Gets the OTLP gRPC endpoint for sending telemetry.
    /// </summary>
    public string OtlpEndpoint => $"http://localhost:{OtlpGrpcPort}";

    /// <summary>
    /// Starts the Aspire Dashboard in a container.
    /// </summary>
    /// <returns>The dashboard URL, or null if failed.</returns>
    public async Task<string> StartAsync()
    {
        // Detect container runtime
        var runtime = await DetectContainerRuntimeAsync();
        if (runtime == null)
        {
            AnsiConsole.MarkupLine("[red]No container runtime found![/]");
            AnsiConsole.MarkupLine("[grey]Please install Podman or Docker and ensure it's running.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[grey]Using container runtime:[/] [cyan]{runtime}[/]");

        // Check if ports are available
        if (!await EnsurePortsAvailableAsync(runtime))
        {
            return null;
        }

        // Pull the image if needed (with progress)
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Pulling Aspire Dashboard image...", async ctx =>
            {
                await PullImageAsync(runtime);
            });

        // Start the container
        var started = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Starting Aspire Dashboard...", async ctx =>
            {
                return await StartContainerAsync(runtime);
            });

        if (!started)
        {
            return null;
        }

        // Wait for dashboard to be ready
        var ready = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Waiting for dashboard to be ready...", async ctx =>
            {
                return await WaitForDashboardAsync();
            });

        if (!ready)
        {
            AnsiConsole.MarkupLine("[yellow]Dashboard may not be fully ready, but continuing...[/]");
        }

        DashboardUrl = $"http://localhost:{DashboardPort}";
        return DashboardUrl;
    }

    /// <summary>
    /// Stops the Aspire Dashboard container.
    /// </summary>
    public async Task StopAsync()
    {
        var runtime = await DetectContainerRuntimeAsync();
        if (runtime == null)
        {
            return;
        }

        try
        {
            var stopProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtime,
                    Arguments = $"stop {ContainerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            stopProcess.Start();
            await stopProcess.WaitForExitAsync();

            var rmProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtime,
                    Arguments = $"rm {ContainerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            rmProcess.Start();
            await rmProcess.WaitForExitAsync();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static async Task<string> DetectContainerRuntimeAsync()
    {
        // Try podman first (user's preference)
        if (await IsCommandAvailableAsync("podman"))
        {
            return "podman";
        }

        // Fall back to docker
        if (await IsCommandAvailableAsync("docker"))
        {
            return "docker";
        }

        return null;
    }

    private static async Task<bool> IsCommandAvailableAsync(string command)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsurePortsAvailableAsync(string runtime)
    {
        // Check if our container is already running
        var listProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = runtime,
                Arguments = $"ps -a --filter name={ContainerName} --format \"{{{{.Names}}}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        listProcess.Start();
        var output = await listProcess.StandardOutput.ReadToEndAsync();
        await listProcess.WaitForExitAsync();

        if (output.Contains(ContainerName))
        {
            AnsiConsole.MarkupLine("[grey]Stopping existing dashboard container...[/]");
            await StopAsync();
            Thread.Sleep(2000);
        }

        // Check if ports are in use by something else
        if (IsPortInUse(DashboardPort))
        {
            AnsiConsole.MarkupLine($"[red]Port {DashboardPort} is already in use![/]");
            return false;
        }

        if (IsPortInUse(OtlpGrpcPort))
        {
            AnsiConsole.MarkupLine($"[red]Port {OtlpGrpcPort} is already in use![/]");
            return false;
        }

        return true;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static async Task PullImageAsync(string runtime)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = runtime,
                Arguments = $"pull {ImageName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync();
    }

    private async Task<bool> StartContainerAsync(string runtime)
    {
        try
        {
            // Aspire Dashboard configuration:
            // - Port 18888: Dashboard UI
            // - Port 4317: OTLP gRPC endpoint (mapped to internal 18889)
            // - DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS: Disable auth for local dev
            var arguments = $"run -d --rm " +
                            $"--name {ContainerName} " +
                            $"-p {DashboardPort}:18888 " +
                            $"-p {OtlpGrpcPort}:18889 " +
                            $"-e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true " +
                            $"{ImageName}";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtime,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Failed to start container:[/] {error}");
                return false;
            }

            _containerProcess = process;
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error starting container:[/] {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WaitForDashboardAsync()
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var maxAttempts = 30; // 30 seconds max

        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                var response = await httpClient.GetAsync($"http://localhost:{DashboardPort}");
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Not ready yet
            }

            await Task.Delay(1000);
        }

        return false;
    }
}
