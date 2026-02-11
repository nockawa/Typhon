namespace Typhon.Shell.Commands;

/// <summary>
/// Result of executing a shell command.
/// </summary>
internal readonly struct CommandResult
{
    public bool Success { get; }
    public string Output { get; }
    public bool ShouldExit { get; }

    /// <summary>
    /// When true, Output contains Spectre.Console markup and should be rendered with AnsiConsole.MarkupLine.
    /// When false, Output is plain text and should be rendered with Console.WriteLine.
    /// </summary>
    public bool UseMarkup { get; }

    private CommandResult(bool success, string output, bool shouldExit, bool useMarkup = false)
    {
        Success = success;
        Output = output;
        ShouldExit = shouldExit;
        UseMarkup = useMarkup;
    }

    public static CommandResult Ok(string output = null) => new(true, output, false);
    public static CommandResult Markup(string output) => new(true, output, false, true);
    public static CommandResult Error(string message) => new(false, message, false);
    public static CommandResult Exit() => new(true, null, true);

    /// <summary>
    /// Returns a new result with the given text appended to the output.
    /// </summary>
    public CommandResult WithAppendedOutput(string extra)
    {
        var combined = Output != null ? Output + "\n" + extra : extra;
        return new CommandResult(Success, combined, ShouldExit, UseMarkup);
    }
}
