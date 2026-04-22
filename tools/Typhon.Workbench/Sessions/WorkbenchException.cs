namespace Typhon.Workbench.Sessions;

public sealed class WorkbenchException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    public WorkbenchException(int statusCode, string errorCode, string detail, Exception inner = null)
        : base(detail, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
