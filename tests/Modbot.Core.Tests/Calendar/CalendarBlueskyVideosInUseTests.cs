using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// The kept videos Bluesky's posts still need, as the clean-up asks for them. A template keeps its video
/// inside its fields JSON, so these go through <see cref="CalendarBlueskyVideosInUse.ReadAsync"/> with templates
/// saved by hand. The database is shared, so each test looks for its own ids rather than the whole set.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarBlueskyVideosInUseTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Evening = new(2026, 10, 17, 21, 0, 0, TimeSpan.Zero);

    private async Task SaveTemplateAsync(string fields)
    {
        await using var context = db.NewContext();
        context.CalendarTemplates.Add(new CalendarTemplate
        {
            Id = Guid.CreateVersion7(),
            Name = $"Template {Guid.NewGuid()}",
            Fields = fields,
            CreatedAt = Evening,
            UpdatedAt = Evening,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<HashSet<Guid>> ReadAsync()
    {
        await using var context = db.NewContext();
        return await CalendarBlueskyVideosInUse.ReadAsync(context, Ct);
    }

    [Fact]
    public async Task ATemplatesVideoIsInUse()
    {
        var id = Guid.CreateVersion7();
        await SaveTemplateAsync($$$"""{"title":"Movie night","blueskyVideo":{"videoId":"{{{id}}}","url":null,"alt":"The trailer"}}""");

        Assert.Contains(id, await ReadAsync());
    }

    [Fact]
    public async Task TheNamesInATemplateAreReadWhateverTheirCase()
    {
        var id = Guid.CreateVersion7();
        await SaveTemplateAsync($$$"""{"BlueskyVideo":{"VideoId":"{{{id}}}"}}""");

        Assert.Contains(id, await ReadAsync());
    }

    [Fact]
    public async Task ATemplateWithAVideoLinkOrNoVideoKeepsNoFile()
    {
        var linked = Guid.CreateVersion7();
        var picture = Guid.CreateVersion7();
        await SaveTemplateAsync($$"""{"blueskyVideo":{"videoId":null,"url":"https://videos.example/a.mp4","alt":""},"other":"{{linked}}"}""");
        await SaveTemplateAsync($$"""{"blueskyVideo":null,"blueskyPictures":[{"pictureId":"{{picture}}","alt":""}]}""");

        var inUse = await ReadAsync();

        Assert.DoesNotContain(linked, inUse);
        Assert.DoesNotContain(picture, inUse);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"blueskyVideo":"nope"}""")]
    [InlineData("""{"blueskyVideo":{"videoId":"not an id"}}""")]
    [InlineData("""{"blueskyVideo":{"videoId":42}}""")]
    public async Task ATemplateThatCannotBeReadDoesNotStopTheRest(string fields)
    {
        var id = Guid.CreateVersion7();
        await SaveTemplateAsync(fields);
        await SaveTemplateAsync($$$"""{"blueskyVideo":{"videoId":"{{{id}}}"}}""");

        Assert.Contains(id, await ReadAsync());
    }
}
