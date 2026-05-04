using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// File-backed persistence + hot-reload for <see cref="WorkbenchOptions"/>. Atomic write via
/// temp file + rename so a crash mid-save can't corrupt the JSON. <see cref="FileSystemWatcher"/>
/// handles out-of-band edits (e.g., user editing the JSON by hand): debounced 200 ms to coalesce
/// the multi-event burst most editors emit on save.
///
/// Storage path: <c>%LOCALAPPDATA%\Typhon.Workbench\options.json</c> on Windows,
/// <c>~/Library/Application Support/Typhon.Workbench/options.json</c> on macOS.
/// </summary>
public sealed class OptionsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly string _filePath;
    private readonly ILogger<OptionsStore> _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly object _lock = new();
    private readonly System.Threading.Timer _debounceTimer;
    private WorkbenchOptions _current;

    /// <summary>Fired when the on-disk file changes (after debounce + reload).</summary>
    public event Action<WorkbenchOptions> OptionsChanged;

    public OptionsStore(ILogger<OptionsStore> logger) : this(logger, DefaultDirectory()) { }

    /// <summary>Test-friendly constructor: store options in <paramref name="directory"/> instead of LocalApplicationData.</summary>
    public OptionsStore(ILogger<OptionsStore> logger, string directory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "options.json");

        _current = TryLoad() ?? new WorkbenchOptions();

        var watcherDir = dir;
        _watcher = new FileSystemWatcher(watcherDir, "options.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += (s, e) => OnFileChanged(s, e);

        _debounceTimer = new System.Threading.Timer(_ => DebouncedReload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath => _filePath;

    public WorkbenchOptions Get()
    {
        lock (_lock) { return _current; }
    }

    /// <summary>Replace the whole options document. Atomic on disk; broadcasts to subscribers.</summary>
    public void Replace(WorkbenchOptions next)
    {
        if (next == null) throw new ArgumentNullException(nameof(next));
        lock (_lock)
        {
            _current = next;
            WriteAtomic(next);
        }
        OptionsChanged?.Invoke(next);
    }

    /// <summary>Patch the Editor sub-record. Other categories untouched.</summary>
    public void PatchEditor(EditorOptions editor)
    {
        if (editor == null) throw new ArgumentNullException(nameof(editor));
        WorkbenchOptions next;
        lock (_lock)
        {
            next = _current with { Editor = editor };
            _current = next;
            WriteAtomic(next);
        }
        OptionsChanged?.Invoke(next);
    }

    /// <summary>Patch the Profiler sub-record. Other categories untouched.</summary>
    public void PatchProfiler(ProfilerOptions profiler)
    {
        if (profiler == null) throw new ArgumentNullException(nameof(profiler));
        WorkbenchOptions next;
        lock (_lock)
        {
            next = _current with { Profiler = profiler };
            _current = next;
            WriteAtomic(next);
        }
        OptionsChanged?.Invoke(next);
    }

    private WorkbenchOptions TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            using var stream = File.OpenRead(_filePath);
            return JsonSerializer.Deserialize<WorkbenchOptions>(stream, JsonOpts);
        }
        catch (Exception ex)
        {
            // Treat parse / IO errors as "no file" — keep defaults rather than crashing the host.
            _logger.LogWarning(ex, "Failed to load options from {Path}; using defaults.", _filePath);
            return null;
        }
    }

    /// <summary>
    /// Atomic write: serialize to a temp file, then rename over the target. A crash mid-write leaves
    /// either the old file (if temp not yet renamed) or the new file (rename is atomic on most FSes).
    /// </summary>
    private void WriteAtomic(WorkbenchOptions options)
    {
        try
        {
            var tempPath = _filePath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, options, JsonOpts);
            }
            // File.Move with overwrite is the closest cross-platform atomic-rename .NET offers.
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist options to {Path}.", _filePath);
            throw;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce — editors typically emit multiple change events on save (write, flush, attribute).
        _debounceTimer.Change(dueTime: 200, period: Timeout.Infinite);
    }

    private void DebouncedReload()
    {
        WorkbenchOptions next;
        lock (_lock)
        {
            var loaded = TryLoad();
            if (loaded == null || loaded.Equals(_current))
            {
                return;
            }
            next = loaded;
            _current = loaded;
        }
        OptionsChanged?.Invoke(next);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }

    /// <summary>Default options directory: <c>%LOCALAPPDATA%\Typhon.Workbench</c> on Windows, equivalents on macOS / Linux.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Typhon.Workbench");
}
