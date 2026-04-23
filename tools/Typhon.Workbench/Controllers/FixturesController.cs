#if DEBUG
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// DEBUG-only dev-fixture endpoint. Calls the internal <see cref="FixtureDatabase.CreateOrReuse"/>
/// to produce (or reuse) a populated Workbench test database under the user's local app data
/// folder, so the "Dev Fixture" Connect tab can instantly open a real-content DB without the user
/// having to run the NUnit generator manually.
///
/// Gated by <c>#if DEBUG</c> so this surface never ships in a Release build of the Workbench. The
/// client detects availability via the capability probe at <see cref="GetCapability"/>.
/// </summary>
[ApiController]
[Route("api/fixtures")]
[Tags("Fixtures")]
[RequireBootstrapToken]
public sealed class FixturesController : ControllerBase
{
    /// <summary>Capability probe — lets the client decide whether to render the Dev Fixture tab.</summary>
    [HttpGet("capability")]
    public ActionResult<FixtureCapabilityDto> GetCapability()
        => Ok(new FixtureCapabilityDto(Available: true, OutputDirectory: DefaultOutputDirectory()));

    /// <summary>
    /// Create (or reuse) the Workbench dev fixture database. When <paramref name="req"/>.Force is
    /// <c>false</c> and the database already exists, returns its path without regenerating — the
    /// Dev Fixture tab default. When <c>true</c>, wipes the output directory and rebuilds.
    /// </summary>
    [HttpPost("create")]
    public ActionResult<CreateFixtureResponseDto> Create([FromBody] CreateFixtureRequestDto req)
    {
        var outDir = string.IsNullOrWhiteSpace(req?.OutputDirectory)
            ? DefaultOutputDirectory()
            : req.OutputDirectory;

        var result = FixtureDatabase.CreateOrReuse(outDir, force: req?.Force ?? false);

        return Ok(new CreateFixtureResponseDto(
            TyphonFilePath: result.TyphonFilePath,
            SchemaDllPath: result.SchemaDllPath,
            TotalEntities: result.TotalEntities,
            WasCreated: result.WasCreated));
    }

    /// <summary>
    /// Default output directory for dev fixtures — follows the same per-user local-state convention
    /// as the bootstrap token file. On POSIX hosts uses <c>$XDG_DATA_HOME/typhon/workbench/fixtures/</c>
    /// (or <c>~/.local/share/typhon/workbench/fixtures/</c> as a fallback), on Windows uses
    /// <c>%LOCALAPPDATA%\Typhon\Workbench\Fixtures\</c>.
    /// </summary>
    private static string DefaultOutputDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Typhon", "Workbench", "Fixtures", "base-tests");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(xdg, "typhon", "workbench", "fixtures", "base-tests");
    }
}

/// <summary>Client-facing advertisement of the dev-fixture capability + the default target directory.</summary>
public sealed record FixtureCapabilityDto(bool Available, string OutputDirectory);

/// <summary>Request body for <see cref="FixturesController.Create"/>.</summary>
public sealed record CreateFixtureRequestDto(bool Force, string OutputDirectory);

/// <summary>Response body for <see cref="FixturesController.Create"/>.</summary>
public sealed record CreateFixtureResponseDto(
    string TyphonFilePath,
    string SchemaDllPath,
    int TotalEntities,
    bool WasCreated);
#endif
