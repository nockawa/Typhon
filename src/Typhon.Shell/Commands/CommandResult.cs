namespace Typhon.Shell.Commands;

/// <summary>
/// Result of executing a shell command.
/// </summary>
internal readonly struct CommandResult
{
    public bool Success { get; }
    public string Output { get; }
    public bool ShouldExit { get; }

    internal CommandResult(bool success, string output, bool shouldExit)
    {
        Success = success;
        Output = output;
        ShouldExit = shouldExit;
    }

    public static CommandResult Ok(string output = null) => new(true, output, false);
    public static CommandResult Error(string message) => new(false, message, false);
    public static CommandResult Exit() => new(true, null, true);

    /// <summary>
    /// Returns a new result with the given text appended to the output.
    /// </summary>
    public CommandResult WithAppendedOutput(string extra)
    {
        var combined = Output != null ? Output + "\n" + extra : extra;
        return new CommandResult(Success, combined, ShouldExit);
    }
}
