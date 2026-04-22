using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Typhon.Workbench.Security;

/// <summary>
/// Startup-generated shared secret required on every authoritative API endpoint. The token defeats
/// browser-origin attacks (a malicious page can issue fetches to <c>http://localhost:5200</c>, but
/// the browser sandbox prevents it from reading the token file); legitimate clients — the bundled
/// SPA served by Kestrel and the Vite dev proxy — read the token from
/// <c>%LOCALAPPDATA%\Typhon\Workbench\bootstrap.token</c> (or the XDG equivalent on Linux/macOS)
/// and attach it as the <see cref="HeaderName"/> header on every request.
///
/// The token is regenerated each time the Workbench process starts, so a leaked token from a past
/// session does not compromise the current one.
///
/// SSE endpoints are intentionally NOT gated by this token (EventSource cannot attach custom
/// headers). They rely on the <c>X-Session-Token</c> / URL sessionId being a 128-bit random value
/// obtained via an already-gated <c>POST /api/sessions/*</c> call — an attacker without the
/// bootstrap token cannot create a session and therefore cannot produce a usable SSE URL.
/// </summary>
public sealed class BootstrapTokenGate
{
    public const string HeaderName = "X-Workbench-Token";

    /// <summary>Hex-encoded 256-bit random secret. Stable for the lifetime of this process.</summary>
    public string Token { get; }

    /// <summary>Absolute path of the file where the token was persisted.</summary>
    public string TokenFilePath { get; }

    private readonly byte[] _tokenBytes;

    public BootstrapTokenGate() : this(DefaultTokenDirectory())
    {
    }

    /// <summary>
    /// Alternate constructor used by integration tests to pin the token file to an isolated
    /// directory — otherwise parallel test hosts and a running dev server would race each other
    /// on the single <c>%LOCALAPPDATA%\Typhon\Workbench\bootstrap.token</c> file, with the
    /// last-writer-wins token silently diverging from any other host's in-memory value.
    /// </summary>
    public BootstrapTokenGate(string tokenDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenDirectory);

        var raw = RandomNumberGenerator.GetBytes(32);
        Token = Convert.ToHexString(raw);
        _tokenBytes = Encoding.UTF8.GetBytes(Token);

        Directory.CreateDirectory(tokenDirectory);
        TokenFilePath = Path.Combine(tokenDirectory, "bootstrap.token");

        // Atomic write: same volume, File.Move overwrites in one syscall on Windows/POSIX.
        var tmp = TokenFilePath + ".tmp";
        File.WriteAllText(tmp, Token);
        File.Move(tmp, TokenFilePath, overwrite: true);
    }

    public static string DefaultTokenDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Typhon",
        "Workbench");

    public bool Validate(StringValues header)
    {
        if (header.Count != 1)
        {
            return false;
        }
        var provided = header.ToString();
        if (provided.Length != Token.Length)
        {
            return false;
        }
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(providedBytes, _tokenBytes);
    }
}
