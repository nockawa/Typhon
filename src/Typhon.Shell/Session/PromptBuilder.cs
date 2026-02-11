namespace Typhon.Shell.Session;

/// <summary>
/// Generates the dynamic prompt string based on session state.
/// </summary>
internal static class PromptBuilder
{
    public static string Build(ShellSession session)
    {
        if (!session.IsOpen)
        {
            return "tsh> ";
        }

        if (!session.HasTransaction)
        {
            return $"tsh:{session.DatabaseName}> ";
        }

        var tsn = session.Transaction.TSN;
        var dirty = session.IsDirty ? "*" : "";
        return $"tsh:{session.DatabaseName}[tx:{tsn}{dirty}]> ";
    }
}
