namespace Modbot.Api.Features.TermsAndRules;

/// <summary>A term as staff fill it in.</summary>
/// <param name="Name">The word. At most 100 characters, on one line.</param>
/// <param name="OtherNames">Other names that mean the same, one entry each: at most 10 of at most 50 characters.</param>
/// <param name="Meaning">What it means. At most 1,000 characters.</param>
public sealed record TermRequest(string? Name, IReadOnlyList<string>? OtherNames, string? Meaning);

/// <summary>A term.</summary>
public sealed record TermView(Guid Id, string Name, IReadOnlyList<string> OtherNames, string Meaning, DateTimeOffset UpdatedAt);

/// <summary>Every term, by name.</summary>
/// <param name="CanEdit">Whether the signed-in account may add, change and delete terms.</param>
public sealed record TermsView(IReadOnlyList<TermView> Terms, bool CanEdit);

/// <summary>A rule as staff fill it in.</summary>
/// <param name="Title">At most 100 characters, on one line.</param>
/// <param name="Text">At most 1,500 characters.</param>
public sealed record RuleRequest(string? Title, string? Text);

/// <summary>Which way to move a rule: <c>up</c> or <c>down</c>.</summary>
public sealed record RuleMoveRequest(string? Direction);

/// <summary>A rule.</summary>
/// <param name="Number">Its place in the list, from 1. Moving a rule renumbers the ones between.</param>
public sealed record RuleView(Guid Id, int Number, string Title, string Text, DateTimeOffset UpdatedAt);

/// <summary>Every rule, in order.</summary>
/// <param name="CanEdit">Whether the signed-in account may add, change, move and delete rules.</param>
public sealed record RulesView(IReadOnlyList<RuleView> Rules, bool CanEdit);
