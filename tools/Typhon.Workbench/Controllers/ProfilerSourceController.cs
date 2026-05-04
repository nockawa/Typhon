using System.Text;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Workbench-wide profiler operations that aren't tied to a specific session: the
/// "Open in editor" handoff and the inline source-preview fetch. See
/// claude/design/observability/09-profiler-source-attribution.md §5.5 + §5.6.
/// </summary>
[ApiController]
[Route("api/profiler")]
[Tags("Profiler")]
[RequireBootstrapToken]
public sealed class ProfilerSourceController : ControllerBase
{
    private readonly OptionsStore _options;
    private readonly EditorLauncher _launcher;

    public ProfilerSourceController(OptionsStore options, EditorLauncher launcher)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    /// <summary>
    /// Report the workspace root the server will use to resolve repo-relative paths from trace
    /// manifests. When the user has set <see cref="ProfilerOptions.WorkspaceRoot"/> we return that
    /// verbatim; otherwise we auto-detect by walking up from CWD looking for a <c>.git</c> entry.
    /// The Profiler options form displays this so the user can see what will resolve.
    /// </summary>
    [HttpGet("workspace-root")]
    public ActionResult<WorkspaceRootDto> GetWorkspaceRoot()
    {
        var configured = _options.Get().Profiler.WorkspaceRoot;
        if (!string.IsNullOrEmpty(configured))
        {
            return Ok(new WorkspaceRootDto(Effective: configured, Source: "configured"));
        }
        var detected = AutoDetectRepoRoot();
        if (detected != null)
        {
            return Ok(new WorkspaceRootDto(Effective: detected, Source: "auto-detected"));
        }
        return Ok(new WorkspaceRootDto(Effective: Directory.GetCurrentDirectory(), Source: "cwd-fallback"));
    }

    /// <summary>
    /// Launch the user's configured editor at the given file:line. <paramref name="file"/> is a
    /// repo-relative path from the trace manifest (e.g. "/_/src/.../BTree.cs"). The server joins
    /// it with the configured workspace root and dispatches to <see cref="EditorLauncher"/>.
    /// </summary>
    [HttpPost("open-in-editor")]
    public ActionResult<OpenInEditorResult> OpenInEditor([FromBody] OpenInEditorRequest body)
    {
        if (body == null) return BadRequest(new OpenInEditorResult(false, "Body required", ""));
        if (string.IsNullOrWhiteSpace(body.File))
        {
            return BadRequest(new OpenInEditorResult(false, "File path required", ""));
        }
        if (body.Line <= 0)
        {
            return BadRequest(new OpenInEditorResult(false, "Line must be > 0", ""));
        }

        var opts = _options.Get();
        var absolutePath = ResolveAbsolutePath(body.File, opts.Profiler.WorkspaceRoot);

        var result = _launcher.Launch(opts.Editor, absolutePath, body.Line, body.Column);
        if (!result.Success)
        {
            return Ok(new OpenInEditorResult(false, result.ErrorMessage, result.Hint));
        }
        return Ok(new OpenInEditorResult(true, "", ""));
    }

    /// <summary>
    /// Fetch a window of source lines around <paramref name="line"/> from the file at <paramref name="path"/>.
    /// <paramref name="path"/> is a repo-relative path from the trace manifest. Path-traversal guarded:
    /// the resolved absolute path must remain inside the configured workspace root. <paramref name="context"/>
    /// is the number of lines on each side of <paramref name="line"/> (default 20, capped at 100).
    /// </summary>
    [HttpGet("source")]
    public ActionResult<SourceWindowDto> GetSource([FromQuery] string path, [FromQuery] int line, [FromQuery] int? context)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "path query parameter required" });
        }
        if (line <= 0)
        {
            return BadRequest(new { error = "line must be > 0" });
        }
        var ctx = Math.Clamp(context ?? 20, 1, 100);

        var opts = _options.Get();
        var workspaceRoot = string.IsNullOrEmpty(opts.Profiler.WorkspaceRoot)
            ? AutoDetectRepoRoot() ?? Directory.GetCurrentDirectory()
            : opts.Profiler.WorkspaceRoot;
        var absolutePath = ResolveAbsolutePath(path, workspaceRoot);

        // Path-traversal guard: the resolved path must sit under the workspace root.
        var fullRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(absolutePath);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "path is outside the workspace root", workspaceRoot = fullRoot });
        }
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { error = $"File not found: {fullPath}" });
        }

        var allLines = System.IO.File.ReadAllLines(fullPath, Encoding.UTF8);
        var startLine = Math.Max(1, line - ctx);
        var endLine = Math.Min(allLines.Length, line + ctx);
        var window = new string[endLine - startLine + 1];
        Array.Copy(allLines, startLine - 1, window, 0, window.Length);

        return Ok(new SourceWindowDto(
            File: path,
            Line: line,
            StartLine: startLine,
            EndLine: endLine,
            Lines: window));
    }

    /// <summary>
    /// Strip the design's "/_/" repo-relative prefix and join with the workspace root. Trim leading
    /// path separators on the relative portion to keep <see cref="Path.Combine"/> well-behaved.
    /// When <paramref name="workspaceRoot"/> is empty, auto-detects the repo root by walking up from
    /// CWD looking for a <c>.git</c> directory; otherwise falls back to CWD itself. The Workbench's
    /// CWD is typically <c>tools/Typhon.Workbench/</c> when launched via <c>dotnet run</c>, which
    /// would resolve <c>/_/src/Typhon.Engine/...</c> to a non-existent path — auto-detect avoids that.
    /// </summary>
    public static string ResolveAbsolutePath(string repoRelative, string workspaceRoot)
    {
        var relative = repoRelative;
        if (relative.StartsWith("/_/", StringComparison.Ordinal))
        {
            relative = relative.Substring(3);
        }
        relative = relative.TrimStart('/', '\\');
        if (string.IsNullOrEmpty(workspaceRoot))
        {
            workspaceRoot = AutoDetectRepoRoot() ?? Directory.GetCurrentDirectory();
        }
        return Path.GetFullPath(Path.Combine(workspaceRoot, relative));
    }

    /// <summary>
    /// Walk up from <see cref="Directory.GetCurrentDirectory"/> looking for a directory that
    /// contains a <c>.git</c> entry (file or folder — submodules and worktrees use a file). Returns
    /// the matching directory or <c>null</c> if none is found.
    /// </summary>
    public static string AutoDetectRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || System.IO.File.Exists(gitPath))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public sealed record OpenInEditorRequest(string File, int Line, int? Column);
    public sealed record OpenInEditorResult(bool Ok, string Error, string Hint);
    public sealed record SourceWindowDto(string File, int Line, int StartLine, int EndLine, string[] Lines);
    public sealed record WorkspaceRootDto(string Effective, string Source);
}
