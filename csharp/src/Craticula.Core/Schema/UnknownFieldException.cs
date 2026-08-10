namespace Craticula.Core.Schema;

/// <summary>SC-1/SEC-1: thrown in <see cref="GridSchemaMode.Strict"/> mode when one or more
/// referenced colIds are absent from the <see cref="GridSchema"/>'s field allowlist.</summary>
public sealed class UnknownFieldException : Exception
{
    public IReadOnlyList<string> ColIds { get; }

    public UnknownFieldException(IReadOnlyList<string> colIds)
        : base(BuildMessage(colIds))
    {
        ColIds = colIds;
    }

    private static string BuildMessage(IReadOnlyList<string> colIds) =>
        $"Unknown colId(s) not present in the GridSchema field allowlist (SC-1): {string.Join(", ", colIds)}";
}
