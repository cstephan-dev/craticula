namespace Craticula.Core.Schema;

/// <summary>Result of a <see cref="GridSchemaMode.Lenient"/> allowlist check: the referenced colIds
/// split into those present in the schema and those stripped for being unknown.</summary>
public sealed record AllowlistResult(IReadOnlyList<string> Allowed, IReadOnlyList<string> Stripped);
