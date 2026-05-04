using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// Launches the user's configured editor at a given file:line via <c>Process.Start</c>. Per-OS
/// dispatch table — see claude/design/observability/09-profiler-source-attribution.md §5.5.
///
/// Officially supported platforms: Windows + macOS (matches the design's scope cut). Linux is
/// implemented best-effort but documented as untested-on-this-machine.
///
/// Cross-platform launching:
/// - URL schemes (vscode://, cursor://): on Windows uses <c>UseShellExecute=true</c> against the
///   registered protocol handler; on macOS goes through <c>open &lt;url&gt;</c> for stability.
/// - Native commands (rider64.exe, devenv.exe): direct <c>Process.Start</c> with explicit argv.
/// - Custom argv template: tokens <c>{file}</c>, <c>{line}</c>, <c>{column}</c> substituted as
///   discrete argv elements — never executed via a shell, so no injection.
/// </summary>
public sealed class EditorLauncher
{
    /// <summary>
    /// Launch the configured editor at the given file:line. Returns success or a structured error
    /// the controller can surface to the client.
    /// </summary>
    public LaunchResult Launch(EditorOptions options, string absolutePath, int line, int? column)
    {
        if (options == null) return LaunchResult.Error("No editor options configured");
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return LaunchResult.Error("No file path provided");
        }
        try
        {
            return options.Kind switch
            {
                EditorKind.VsCode => LaunchUrlScheme("vscode", absolutePath, line),
                EditorKind.Cursor => LaunchUrlScheme("cursor", absolutePath, line),
                EditorKind.Rider => LaunchRider(absolutePath, line),
                EditorKind.VisualStudio => LaunchVisualStudio(absolutePath, line),
                EditorKind.Custom => LaunchCustom(options.CustomCommand, absolutePath, line, column),
                _ => LaunchResult.Error($"Unknown editor kind: {options.Kind}"),
            };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.NativeErrorCode switch
            {
                2 => LaunchResult.Error($"Editor binary not found on PATH: {ex.Message}",
                    "Check that the configured editor is installed and accessible. "
                    + "On Windows the JetBrains Toolbox installs `rider.cmd` as a PATH shim; on macOS, the JetBrains Toolbox installs `rider`."),
                1155 => LaunchResult.Error($"No app registered for the URL scheme: {ex.Message}",
                    "Open the editor's Settings → URL Handler (VS Code), Toolbox (Rider), or reinstall."),
                _ => LaunchResult.Error($"Process.Start failed: {ex.Message}"),
            };
        }
        catch (Exception ex)
        {
            return LaunchResult.Error($"Editor launch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// VS Code / Cursor URL-scheme path. <c>vscode://file/&lt;abs&gt;:&lt;line&gt;</c> is the
    /// documented syntax. Forward slashes in the URL on both OSes.
    /// </summary>
    private static LaunchResult LaunchUrlScheme(string scheme, string absolutePath, int line)
    {
        var url = BuildFileUrl(scheme, absolutePath, line);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // On macOS go through `open <url>`; on Linux use xdg-open. Both are the documented
            // way to invoke a registered URL-scheme handler.
            var opener = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
            var psi = new ProcessStartInfo(opener);
            psi.ArgumentList.Add(url);
            psi.UseShellExecute = false;
            using var p = Process.Start(psi);
            return LaunchResult.Ok();
        }
        // Windows: UseShellExecute lets the OS resolve the URL scheme via the registry.
        var psiWin = new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        };
        using var pWin = Process.Start(psiWin);
        return LaunchResult.Ok();
    }

    /// <summary>
    /// Rider: native command-line launch. JetBrains Toolbox installs different shims on different
    /// platforms — `rider.cmd` on Windows (in <c>%LOCALAPPDATA%\JetBrains\Toolbox\scripts</c>, added
    /// to PATH by Toolbox), `rider` on macOS (Toolbox shim) / Linux (tarball install). The bundled
    /// `rider64.exe` lives inside the install dir which is NOT on PATH by default. We try the
    /// well-known names in order so any of these install layouts works.
    /// </summary>
    private static LaunchResult LaunchRider(string absolutePath, int line)
    {
        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "rider.cmd", "rider64.exe", "rider.exe", "rider.bat" }
            : new[] { "rider" };

        System.ComponentModel.Win32Exception lastNotFound = null;
        foreach (var binary in candidates)
        {
            var psi = new ProcessStartInfo(binary);
            psi.ArgumentList.Add("--line");
            psi.ArgumentList.Add(line.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(absolutePath);
            psi.UseShellExecute = false;
            try
            {
                using var p = Process.Start(psi);
                return LaunchResult.Ok();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                lastNotFound = ex;
                // Try next candidate.
            }
        }
        // None of the candidates were found — surface a single descriptive error.
        throw lastNotFound ?? new System.ComponentModel.Win32Exception(2, "rider not found on PATH");
    }

    /// <summary>
    /// Visual Studio: <c>devenv.exe /Edit "&lt;file&gt;" /Command "Edit.GoTo &lt;line&gt;"</c>.
    /// Windows-only. On macOS this returns an error (VS for Mac was discontinued Aug 2024).
    /// </summary>
    private static LaunchResult LaunchVisualStudio(string absolutePath, int line)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return LaunchResult.Error(
                "Visual Studio is not available on this OS.",
                "Use VS Code, Cursor, or Rider. (Visual Studio for Mac was discontinued in August 2024.)");
        }
        var psi = new ProcessStartInfo("devenv.exe");
        psi.ArgumentList.Add("/Edit");
        psi.ArgumentList.Add(absolutePath);
        psi.ArgumentList.Add("/Command");
        psi.ArgumentList.Add($"Edit.GoTo {line}");
        psi.UseShellExecute = false;
        using var p = Process.Start(psi);
        return LaunchResult.Ok();
    }

    /// <summary>
    /// Custom argv template. Splits the template on whitespace into discrete argv elements,
    /// substituting <c>{file}</c>, <c>{line}</c>, <c>{column}</c> as separate elements — never
    /// invokes a shell, so no injection vector.
    /// </summary>
    private static LaunchResult LaunchCustom(string template, string absolutePath, int line, int? column)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return LaunchResult.Error("Custom command template is empty",
                "Set Options → Editor → Custom command (e.g., 'nvim-qt --remote +{line} {file}').");
        }
        var tokens = template.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return LaunchResult.Error("Custom command template is empty");
        }
        var psi = new ProcessStartInfo(tokens[0]);
        for (int i = 1; i < tokens.Length; i++)
        {
            var arg = tokens[i]
                .Replace("{file}", absolutePath)
                .Replace("{line}", line.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{column}", (column ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(arg);
        }
        psi.UseShellExecute = false;
        using var p = Process.Start(psi);
        return LaunchResult.Ok();
    }

    /// <summary>
    /// Build a <c>vscode://file/&lt;abs&gt;:&lt;line&gt;</c>-style URL. Path is URL-encoded enough
    /// to survive the URI; backslashes are converted to forward-slashes (URL convention).
    /// </summary>
    public static string BuildFileUrl(string scheme, string absolutePath, int line)
    {
        var path = absolutePath.Replace('\\', '/');
        // URL-escape spaces and other special chars in the path. We use Uri.EscapeDataString on the
        // path component but preserve the leading '/' segments for readability.
        var parts = path.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        var encodedPath = string.Join('/', parts);
        return $"{scheme}://file/{encodedPath}:{line}";
    }
}

/// <summary>Result of an editor launch — either Ok or a structured error with optional hint.</summary>
public readonly struct LaunchResult
{
    public bool Success { get; }
    public string ErrorMessage { get; }
    public string Hint { get; }

    private LaunchResult(bool success, string error, string hint)
    {
        Success = success;
        ErrorMessage = error ?? string.Empty;
        Hint = hint ?? string.Empty;
    }

    public static LaunchResult Ok() => new(true, null, null);
    public static LaunchResult Error(string message) => new(false, message, null);
    public static LaunchResult Error(string message, string hint) => new(false, message, hint);
}
