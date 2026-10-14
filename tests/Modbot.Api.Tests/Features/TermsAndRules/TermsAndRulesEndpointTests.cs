using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.TermsAndRules;

/// <summary>
/// The Terms and rules API: anybody signed in reads, changing needs Change settings, the limits that
/// keep a /term or /rule answer inside one Discord message are held at save, no two terms answer to
/// the same name, a rule's number is its place, and each change is a fact that names the term or the
/// rule and what was done, never what it says.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class TermsAndRulesEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.GlossaryTerms.ExecuteDeleteAsync(Ct);
            await context.ServerRules.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static async Task<Guid> CreateTermAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/terms", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateRuleAsync(ApiTestHost host, string cookie, string title, string text = "Text.")
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/rules", new { title, text }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    private static async Task<string[]> TitlesAsync(ApiTestHost host, string cookie)
    {
        var listed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/rules", null, cookie, Ct), Ct);
        return [.. listed.GetProperty("rules").EnumerateArray().Select(r => $"{r.GetProperty("number").GetInt32()}. {r.GetProperty("title").GetString()}")];
    }

    private static async Task<int> MovesAsync(ApiTestHost host, params Guid[] rules)
    {
        var moves = 0;
        foreach (var rule in rules)
            moves += (await host.FactsAsync(FactType.RuleChanged, rule.ToString(), Ct)).Count(f => f.Data.Contains("moved"));
        return moves;
    }

    [Fact]
    public async Task Reading_NeedsOnlyASession_AndWritingNeedsChangeSettings()
    {
        await using var host = await StartAsync();
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var terms = await host.SendJsonAsync(HttpMethod.Get, "/api/terms", null, reader, Ct);
        Assert.Equal(HttpStatusCode.OK, terms.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(terms, Ct)).GetProperty("canEdit").GetBoolean());

        var rules = await host.SendJsonAsync(HttpMethod.Get, "/api/rules", null, reader, Ct);
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(rules, Ct)).GetProperty("canEdit").GetBoolean());

        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/terms", new { name = "Regent", meaning = "A helper." }, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/terms/{id}", new { name = "Regent", meaning = "A helper." }, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/terms/{id}", null, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/rules", new { title = "Be kind", text = "Text." }, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/rules/{id}", new { title = "Be kind", text = "Text." }, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/rules/{id}/move", new { direction = "up" }, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/rules/{id}", null, reader, Ct)).StatusCode);

        var seen = await host.SendJsonAsync(HttpMethod.Get, "/api/terms", null, editor, Ct);
        Assert.True((await ApiTestHost.BodyOf(seen, Ct)).GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task ATermIsKept_ListedByName_AndTrimmed()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await CreateTermAsync(host, editor, new { name = "Zebra", meaning = "Striped." });
        await CreateTermAsync(host, editor, new { name = "  apple ", otherNames = new[] { " Apfel ", "", "apple" }, meaning = " A fruit. " });

        var listed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/terms", null, editor, Ct), Ct);
        var terms = listed.GetProperty("terms").EnumerateArray().ToList();

        Assert.Equal(["apple", "Zebra"], terms.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(["Apfel"], terms[0].GetProperty("otherNames").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal("A fruit.", terms[0].GetProperty("meaning").GetString());
    }

    [Fact]
    public async Task TheTermLimits_AreHeldAtSave()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        async Task<HttpResponseMessage> PostAsync(object body)
            => await host.SendJsonAsync(HttpMethod.Post, "/api/terms", body, editor, Ct);

        Assert.Equal("Give the term a name.", await ErrorOf(await PostAsync(new { name = " ", meaning = "x" })));
        Assert.Equal("Say what the term means.", await ErrorOf(await PostAsync(new { name = "A", meaning = "" })));
        Assert.Equal("The name must be on one line.", await ErrorOf(await PostAsync(new { name = "A\nB", meaning = "x" })));
        Assert.Equal($"The name can be at most {GlossaryTerm.MaxNameLength} characters.",
            await ErrorOf(await PostAsync(new { name = new string('n', GlossaryTerm.MaxNameLength + 1), meaning = "x" })));
        Assert.Equal($"The meaning can be at most {GlossaryTerm.MaxMeaningLength} characters.",
            await ErrorOf(await PostAsync(new { name = "A", meaning = new string('m', GlossaryTerm.MaxMeaningLength + 1) })));
        Assert.Equal($"There can be at most {GlossaryTerm.MaxOtherNames} other names.",
            await ErrorOf(await PostAsync(new { name = "A", otherNames = Enumerable.Range(0, GlossaryTerm.MaxOtherNames + 1).Select(i => $"n{i}"), meaning = "x" })));
        Assert.Equal($"An other name can be at most {GlossaryTerm.MaxOtherNameLength} characters.",
            await ErrorOf(await PostAsync(new { name = "A", otherNames = new[] { new string('o', GlossaryTerm.MaxOtherNameLength + 1) }, meaning = "x" })));

        // Right at the limits is fine.
        await CreateTermAsync(host, editor, new
        {
            name = new string('n', GlossaryTerm.MaxNameLength),
            otherNames = Enumerable.Range(0, GlossaryTerm.MaxOtherNames).Select(i => new string((char)('a' + i), GlossaryTerm.MaxOtherNameLength)),
            meaning = new string('m', GlossaryTerm.MaxMeaningLength),
        });

        await using var read = db.NewContext();
        Assert.Equal(1, await read.GlossaryTerms.CountAsync(Ct));
    }

    [Fact]
    public async Task NoTwoTermsAnswerToTheSameName_WhateverTheCase()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var regent = await CreateTermAsync(host, editor, new { name = "Regent", otherNames = new[] { "Reggie" }, meaning = "A helper." });

        var sameName = await host.SendJsonAsync(HttpMethod.Post, "/api/terms", new { name = "regent", meaning = "Other." }, editor, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, sameName.StatusCode);
        Assert.Equal("“regent” is already a name of the term “Regent”.", await ErrorOf(sameName));

        var otherName = await host.SendJsonAsync(HttpMethod.Post, "/api/terms",
            new { name = "Prefect", otherNames = new[] { "REGGIE" }, meaning = "Other." }, editor, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, otherName.StatusCode);
        Assert.Equal("“REGGIE” is already a name of the term “Regent”.", await ErrorOf(otherName));

        // A term may keep its own names when it is saved again.
        var again = await host.SendJsonAsync(HttpMethod.Put, $"/api/terms/{regent}",
            new { name = "Regent", otherNames = new[] { "Reggie" }, meaning = "A helper, changed." }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        await using var read = db.NewContext();
        Assert.Equal(1, await read.GlossaryTerms.CountAsync(Ct));
    }

    [Fact]
    public async Task ATermIsAddedChangedAndDeleted_AndEachIsAFact_ThatNeverHoldsTheMeaning()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var id = await CreateTermAsync(host, editor, new { name = "Regent", meaning = "Secret meaning one." });

        var added = Assert.Single(await host.FactsAsync(FactType.TermChanged, id.ToString(), Ct));
        Assert.Contains("added", added.Data);
        Assert.Contains("Regent", added.Data);

        var changed = await host.SendJsonAsync(HttpMethod.Put, $"/api/terms/{id}",
            new { name = "Regent", meaning = "Secret meaning two." }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/terms/{id}", null, editor, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var facts = await host.FactsAsync(FactType.TermChanged, id.ToString(), Ct);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.DoesNotContain("Secret meaning", f.Data));
        Assert.Contains(facts, f => f.Data.Contains("changed"));
        Assert.Contains(facts, f => f.Data.Contains("deleted"));

        await using var read = db.NewContext();
        Assert.False(await read.GlossaryTerms.AnyAsync(t => t.Id == id, Ct));

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/terms/{id}", null, editor, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/terms/{id}", new { name = "Regent", meaning = "x" }, editor, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheRuleLimits_AreHeldAtSave()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        async Task<HttpResponseMessage> PostAsync(object body)
            => await host.SendJsonAsync(HttpMethod.Post, "/api/rules", body, editor, Ct);

        Assert.Equal("Give the rule a title.", await ErrorOf(await PostAsync(new { title = " ", text = "x" })));
        Assert.Equal("Write the rule.", await ErrorOf(await PostAsync(new { title = "A", text = "" })));
        Assert.Equal("The title must be on one line.", await ErrorOf(await PostAsync(new { title = "A\nB", text = "x" })));
        Assert.Equal($"The title can be at most {ServerRule.MaxTitleLength} characters.",
            await ErrorOf(await PostAsync(new { title = new string('t', ServerRule.MaxTitleLength + 1), text = "x" })));
        Assert.Equal($"The rule can be at most {ServerRule.MaxTextLength} characters.",
            await ErrorOf(await PostAsync(new { title = "A", text = new string('x', ServerRule.MaxTextLength + 1) })));

        var fits = await PostAsync(new { title = new string('t', ServerRule.MaxTitleLength), text = new string('x', ServerRule.MaxTextLength) });
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
    }

    [Fact]
    public async Task RulesAreNumberedFromOne_InTheOrderTheyWereAdded()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await CreateRuleAsync(host, editor, "Be kind");
        await CreateRuleAsync(host, editor, "No spam");
        await CreateRuleAsync(host, editor, "Stay on topic");

        Assert.Equal(["1. Be kind", "2. No spam", "3. Stay on topic"], await TitlesAsync(host, editor));
    }

    [Fact]
    public async Task MovingARule_RenumbersTheOnesBetween_AndTheEndsStayPut()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var first = await CreateRuleAsync(host, editor, "Be kind");
        await CreateRuleAsync(host, editor, "No spam");
        var third = await CreateRuleAsync(host, editor, "Stay on topic");

        var up = await host.SendJsonAsync(HttpMethod.Post, $"/api/rules/{third}/move", new { direction = "up" }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal(["1. Be kind", "2. Stay on topic", "3. No spam"], await TitlesAsync(host, editor));

        var down = await host.SendJsonAsync(HttpMethod.Post, $"/api/rules/{first}/move", new { direction = "down" }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Equal(["1. Stay on topic", "2. Be kind", "3. No spam"], await TitlesAsync(host, editor));
        Assert.Equal(2, await MovesAsync(host, first, third));

        // The first cannot go up: the list is unchanged and nothing more is recorded.
        var top = await host.SendJsonAsync(HttpMethod.Post, $"/api/rules/{third}/move", new { direction = "up" }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, top.StatusCode);
        Assert.Equal(["1. Stay on topic", "2. Be kind", "3. No spam"], await TitlesAsync(host, editor));
        Assert.Equal(2, await MovesAsync(host, first, third));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/rules/{first}/move", new { direction = "sideways" }, editor, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/rules/{Guid.NewGuid()}/move", new { direction = "up" }, editor, Ct)).StatusCode);
    }

    [Fact]
    public async Task DeletingARule_ClosesTheNumbersUp_AndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        await CreateRuleAsync(host, editor, "Be kind");
        var second = await CreateRuleAsync(host, editor, "No spam", "Secret rule text.");
        await CreateRuleAsync(host, editor, "Stay on topic");

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/rules/{second}", null, editor, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(["1. Be kind", "2. Stay on topic"], await TitlesAsync(host, editor));

        var facts = await host.FactsAsync(FactType.RuleChanged, second.ToString(), Ct);
        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, f => f.Data.Contains("deleted") && f.Data.Contains("No spam"));
        Assert.All(facts, f => Assert.DoesNotContain("Secret rule text", f.Data));

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/rules/{second}", null, editor, Ct)).StatusCode);
    }

    [Fact]
    public async Task ARuleIsChanged_KeepingItsPlace_AndASavedSameRuleRecordsNothing()
    {
        await using var host = await StartAsync();
        var (_, editor) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        await CreateRuleAsync(host, editor, "Be kind");
        var second = await CreateRuleAsync(host, editor, "No spam");

        var same = await host.SendJsonAsync(HttpMethod.Put, $"/api/rules/{second}", new { title = "No spam", text = "Text." }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Single(await host.FactsAsync(FactType.RuleChanged, second.ToString(), Ct));

        var changed = await host.SendJsonAsync(HttpMethod.Put, $"/api/rules/{second}", new { title = "No spamming", text = "Text." }, editor, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(2, (await ApiTestHost.BodyOf(changed, Ct)).GetProperty("number").GetInt32());
        Assert.Equal(["1. Be kind", "2. No spamming"], await TitlesAsync(host, editor));
        Assert.Equal(2, (await host.FactsAsync(FactType.RuleChanged, second.ToString(), Ct)).Count);
    }
}
