using Modbot.Api.Features.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// What the form may ask for in each site's own title and description: blank is the shared words,
/// nothing sent keeps what the event has, and each is held to the limits of the shared words.
/// </summary>
public class CalendarSiteTextRequestTests
{
    private static CalendarEventRequest Request(
        string description = "Bring snacks",
        string? vrchatTitle = null,
        string? vrchatDescription = null,
        string? discordTitle = null,
        string? discordDescription = null,
        string? googleTitle = null,
        string? googleDescription = null,
        string title = "Movie night") =>
        new(
            title, description, "2026-10-01T20:00", "2026-10-01T22:00", "Europe/London",
            "none", [], null, null, "members", "us", null, null, "film_media",
            [], [], [], "group", false, true, false, false, null, false, 10, false,
            VRChatTitle: vrchatTitle, VRChatDescription: vrchatDescription,
            DiscordTitle: discordTitle, DiscordDescription: discordDescription,
            GoogleTitle: googleTitle, GoogleDescription: googleDescription);

    [Fact]
    public void EachSitesOwnWordsAreKeptTrimmed()
    {
        var e = new CalendarEvent();

        var problems = CalendarEndpoints.ApplyAll(
            Request(
                vrchatTitle: " 18+ Hangout w/ Thy Kingdom ", vrchatDescription: "Plain",
                discordTitle: "Discord title", discordDescription: "[Group Link](https://vrc.group/KINGDOM.1234)",
                googleTitle: "Google title", googleDescription: "See https://example.com"),
            e);

        Assert.Empty(problems);
        Assert.Equal("18+ Hangout w/ Thy Kingdom", e.VRChatTitle);
        Assert.Equal("Plain", e.VRChatDescription);
        Assert.Equal("Discord title", e.DiscordTitle);
        Assert.Equal("[Group Link](https://vrc.group/KINGDOM.1234)", e.DiscordDescription);
        Assert.Equal("Google title", e.GoogleTitle);
        Assert.Equal("See https://example.com", e.GoogleDescription);
        Assert.Equal("Movie night", e.Title);
    }

    [Fact]
    public void BlankClearsASitesOwnWords_AndNothingSentKeepsThem()
    {
        var e = new CalendarEvent { VRChatTitle = "VRChat", DiscordTitle = "Discord", GoogleTitle = "Google" };

        var problems = CalendarEndpoints.ApplyAll(Request(vrchatTitle: "  "), e);

        Assert.Empty(problems);
        Assert.Null(e.VRChatTitle);
        Assert.Equal("Discord", e.DiscordTitle);
        Assert.Equal("Google", e.GoogleTitle);
    }

    [Fact]
    public void ATitleMayNotHoldALink_ButAFillInAndADescriptionMay()
    {
        var e = new CalendarEvent();

        var problems = CalendarEndpoints.ApplyAll(
            Request(
                description: "More: {{Google event}}",
                discordDescription: "Join: {{Discord event}}",
                title: "Movie {{ google EVENT }}",
                vrchatTitle: "VRChat {{VRChat group page}}",
                discordTitle: "Discord {{Add our Google calendar}}",
                googleTitle: "Google {{Discord event}}"),
            e);

        Assert.Equal(
            [
                "The title can't hold a link. Put links in the description.",
                "The VRChat title can't hold a link. Put links in the description.",
                "The Discord title can't hold a link. Put links in the description.",
                "The Google title can't hold a link. Put links in the description.",
            ],
            problems);

        // A fill-in stays allowed in a title: Use template replaces it before an event exists.
        Assert.Empty(CalendarEndpoints.ApplyAll(Request(title: "Movie with {{Host}}", discordTitle: "{{Host}}"), new CalendarEvent()));
    }

    [Fact]
    public void EachSiteIsHeldToTheLimitsOfTheSharedWords()
    {
        var e = new CalendarEvent();

        var problems = CalendarEndpoints.ApplyAll(
            Request(
                discordTitle: new string('a', CalendarEvent.MaxTitleLength + 1),
                googleDescription: new string('b', CalendarEvent.MaxDescriptionLength + 1)),
            e);

        Assert.Contains($"The Discord title is longer than {CalendarEvent.MaxTitleLength} characters.", problems);
        Assert.Contains($"The Google description is longer than {CalendarEvent.MaxDescriptionLength} characters.", problems);
        Assert.Null(e.DiscordTitle);
    }

    [Fact]
    public void VRChatsOwnDescriptionIsTheOneVRChatNeeds()
    {
        Assert.Contains(
            CalendarVRChatChecks.NoDescription,
            CalendarEndpoints.ApplyAll(Request(description: ""), new CalendarEvent()));

        Assert.Empty(CalendarEndpoints.ApplyAll(Request(description: "", vrchatDescription: "Only on VRChat"), new CalendarEvent()));
    }
}
