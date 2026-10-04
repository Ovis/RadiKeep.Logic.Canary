namespace Canary.Runner;

internal sealed class CanaryStatus
{
    public required string Result { get; init; }
    public required string Message { get; init; }
    public required string TimestampJst { get; init; }
    public required List<CheckResult> Checks { get; init; }
}

internal sealed record ProgramSchemaIssue(string ProgramId, string Field, string Reason);

internal sealed record ProgramSchemaValidationResult(
    IReadOnlyList<ProgramSchemaIssue> RequiredIssues,
    IReadOnlyDictionary<string, int> OptionalMissingCounts);

internal sealed record CheckResult(string CheckId, string Result, string Message, string ErrorCode);
