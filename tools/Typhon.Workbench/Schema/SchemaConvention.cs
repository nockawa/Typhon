namespace Typhon.Workbench.Schema;

/// <summary>
/// Resolves adjacent schema DLLs next to a <c>.typhon</c> file using the <c>*.schema.dll</c>
/// naming convention. Same directory only — no upward walk.
/// </summary>
public static class SchemaConvention
{
    public static string[] ResolveConventionally(string typhonFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typhonFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(typhonFilePath));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }
        var matches = Directory.EnumerateFiles(directory, "*.schema.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches;
    }
}
