namespace Modbot.Api.Conventions;

/// <summary>
/// Marks a route kept for older clients beside the one that replaced it (API conventions design
/// §4). It still answers exactly as it did; the OpenAPI document marks it deprecated and names the
/// new one.
/// </summary>
/// <param name="Route">The route to use instead, such as <c>DELETE /api/group/roles/{id}</c>.</param>
public sealed record ReplacedBy(string Route);
