namespace Craticula.Core.Schema;

/// <summary>Result of a <see cref="GridSchemaMode.Lenient"/> allowlist check: the referenced colIds
/// split into those <see cref="Allowed"/> (present in the schema AND, #1872, declared
/// <see cref="FieldConfig.Filterable"/>) and those <see cref="Stripped"/> for failing either
/// check — unknown colId or a known one the schema forbids filtering on. Bare colIds throughout;
/// <see cref="FieldAllowlistValidator.Strip"/> matches on this exact string.</summary>
public sealed record AllowlistResult(IReadOnlyList<string> Allowed, IReadOnlyList<string> Stripped);
