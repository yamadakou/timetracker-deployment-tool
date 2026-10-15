namespace Timetracker.Domain.Azure;

/// <summary>ロール割り当て 1 つ分の権限（Azure の permissions の actions / notActions）。</summary>
public sealed record PermissionSet(IReadOnlyList<string> Actions, IReadOnlyList<string> NotActions);
