using System.Text.Json.Serialization;

namespace Craticula.Core.Ir;

/// <summary>
/// AR-5: a node of the filter tree, matching <c>spec/ir.schema.json</c>'s <c>$defs/filterNode</c>
/// (recursive by construction, so <see cref="Craticula.Core.Schema.ResourceLimitsValidator"/> can
/// walk it to count depth/leaves against the configurable SEC-3 limits). Deserialized via System.
/// Text.Json polymorphic serialization, discriminated on <c>type</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AndNode), "and")]
[JsonDerivedType(typeof(OrNode), "or")]
[JsonDerivedType(typeof(NotNode), "not")]
[JsonDerivedType(typeof(ComparisonNode), "comparison")]
public abstract record FilterNode;

/// <summary>
/// AR-5: the shape shared by <see cref="AndNode"/>/<see cref="OrNode"/> — a pragmatic base absent
/// from <c>ir.schema.json</c>'s own <c>$defs</c> (andNode/orNode don't <c>$ref</c> a common shape
/// there), introduced purely so a tree walk (e.g. <c>ResourceLimitsValidator</c>) can treat both
/// uniformly without duplicating the depth/leaf-counting recursion.
/// </summary>
public abstract record CompositeFilterNode : FilterNode
{
    public required IReadOnlyList<FilterNode> Nodes { get; init; }
}

/// <summary>Logical conjunction of child nodes (AR-5: And(nodes[])).</summary>
public sealed record AndNode : CompositeFilterNode;

/// <summary>Logical disjunction of child nodes (AR-5: Or(nodes[])).</summary>
public sealed record OrNode : CompositeFilterNode;

/// <summary>Logical negation of a single child node (AR-5: Not(node)).</summary>
public sealed record NotNode : FilterNode
{
    public required FilterNode Node { get; init; }
}
