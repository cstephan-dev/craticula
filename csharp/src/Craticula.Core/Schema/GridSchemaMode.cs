namespace Craticula.Core.Schema;

/// <summary>
/// SC-1: how the field allowlist reacts to a colId absent from <see cref="GridSchema.Fields"/>.
/// </summary>
public enum GridSchemaMode
{
    /// <summary>[DEFAULT] Reject the request — an unlisted colId throws <see cref="UnknownFieldException"/>.</summary>
    Strict,

    /// <summary>Silently strip unlisted colIds instead of throwing.</summary>
    Lenient
}
