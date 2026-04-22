namespace Typhon.Workbench.Sessions;

/// <summary>
/// Resolves UI-facing session paths to on-disk locations. Phase 3 only knows the bundled "demo" path;
/// Phase 4 adds real file-system validation and a proper file picker. Tests override the directory
/// via DI to keep each test isolated.
/// </summary>
public sealed class DemoDataProvider
{
    public string Directory { get; }

    public DemoDataProvider() : this(Path.Combine(AppContext.BaseDirectory, "DemoData")) { }

    public DemoDataProvider(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Translates a client-supplied filePath into a full path the engine can open. Anything not matching
    /// the bundled demo name is rejected — Phase 3 does not support arbitrary files.
    /// </summary>
    public string Resolve(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (!string.Equals(stem, "demo", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchException(400, "unsupported_file",
                $"Phase 3 only supports the bundled demo database; got '{filePath}'.");
        }

        return Path.Combine(Directory, "demo.typhon");
    }
}
