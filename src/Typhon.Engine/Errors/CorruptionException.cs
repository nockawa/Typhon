using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Data integrity violation — checksum mismatch, structural corruption, invalid page state.
/// Never transient (inherits default false). Requires human intervention or restore from backup.
/// </summary>
[PublicAPI]
public class CorruptionException : StorageException
{
    /// <summary>
    /// Creates a new <see cref="CorruptionException"/> for the specified component and page.
    /// </summary>
    /// <param name="componentName">Name of the component where corruption was detected.</param>
    /// <param name="pageIndex">Page index where corruption was detected, or -1 if not page-specific.</param>
    /// <param name="detail">Human-readable description of the corruption.</param>
    public CorruptionException(string componentName, int pageIndex, string detail)
        : base(TyphonErrorCode.DataCorruption, $"Corruption in '{componentName}' at page {pageIndex}: {detail}")
    {
        ComponentName = componentName;
        PageIndex = pageIndex;
    }

    /// <summary>Name of the component where corruption was detected.</summary>
    public string ComponentName { get; }

    /// <summary>Page index where corruption was detected, or -1 if not page-specific.</summary>
    public int PageIndex { get; }
}
