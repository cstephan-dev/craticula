namespace Craticula.Core.Schema;

/// <summary>SEM-1: per-field opt-in to case-sensitive comparison; insensitive is the IR-level default.</summary>
public enum CaseSensitivity
{
    Insensitive,
    Sensitive
}

/// <summary>SEM-7: substring is contains' pinned default; analyzed is the explicit per-field opt-in
/// to tokenized relevance matching.</summary>
public enum SearchMode
{
    Substring,
    Analyzed
}

/// <summary>SC-3: per-field text options — only meaningful when <see cref="FieldConfig.Type"/> is
/// <see cref="FieldType.String"/>.</summary>
public sealed record TextOptions
{
    public CaseSensitivity CaseSensitivity { get; init; } = CaseSensitivity.Insensitive;

    public SearchMode SearchMode { get; init; } = SearchMode.Substring;
}
