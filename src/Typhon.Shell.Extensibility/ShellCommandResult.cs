namespace Typhon.Shell.Extensibility;

/// <summary>
/// Result of a shell command execution. Mirrors the shell's internal CommandResult
/// but lives in the extensibility layer so command authors don't depend on shell internals.
/// </summary>
public readonly struct ShellCommandResult
{
    public bool Success { get; private init; }
    public string Output { get; private init; }
    public bool UseMarkup { get; private init; }

    public static ShellCommandResult Ok(string output = null, bool useMarkup = false) => new() { Success = true, Output = output, UseMarkup = useMarkup };
    public static ShellCommandResult Error(string message) => new() { Success = false, Output = message };
}
