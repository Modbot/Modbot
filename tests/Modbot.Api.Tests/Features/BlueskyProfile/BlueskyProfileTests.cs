using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.BlueskyProfile;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.BlueskyProfile;

/// <summary>
/// The Bluesky profile page's server side (posts design §4.2g): the page reads the profile from Bluesky's
/// public API and keeps the answer for ten seconds; a save changes only the name, bio, picture and banner
/// and writes the rest of the record back as it was (the pin included); the limits are checked before
/// Bluesky is asked anything; a 429 is never tried again; a failed save shows on the page and on Health until
/// one goes through; the Posts tab lists what Modbot sent to the account.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BlueskyProfileTests(PostgresFixture db)
{
    private const string Path = "/api/bluesky/profile";
    private const string SettingsPath = "/api/settings/bluesky";
    private const string PutRecord = "com.atproto.repo.putRecord";
    private const string UploadBlob = "com.atproto.repo.uploadBlob";
    private const string GetRecord = "com.atproto.repo.getRecord";
    private const string GetProfile = "app.bsky.actor.getProfile";

    private const ModbotPermissions Everything =
        ModbotPermissions.ManageSettings | ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewPosts;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Blob(string cid) => new()
    {
        ["$type"] = "blob",
        ["ref"] = new JsonObject { ["$link"] = cid },
        ["mimeType"] = "image/jpeg",
        ["size"] = 1234,
    };

    /// <summary>The profile on the account's server: a pin, a self-label and fields Modbot has never heard of.</summary>
    private static JsonObject Baseline() => new()
    {
        ["$type"] = "app.bsky.actor.profile",
        ["displayName"] = "Our group",
        ["description"] = "We run the group.",
        ["avatar"] = Blob("bafkrei-old-avatar"),
        ["banner"] = Blob("bafkrei-old-banner"),
        ["labels"] = new JsonObject
        {
            ["$type"] = "com.atproto.label.defs#selfLabels",
            ["values"] = new JsonArray(new JsonObject { ["val"] = "bot" }),
        },
        ["pinnedPost"] = new JsonObject
        {
            ["$type"] = "com.atproto.repo.strongRef",
            ["uri"] = $"at://{FakeBluesky.Did}/app.bsky.feed.post/3l4pqjzzyde2a",
            ["cid"] = "bafy-post",
        },
        ["createdAt"] = "2024-03-01T10:00:00.000Z",
        ["somethingBlueskyAddsLater"] = new JsonArray(1, 2, 3),
    };

    private static byte[] Picture(int length, params byte[] start)
    {
        var bytes = new byte[length];
        start.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Png(int length) => Picture(length, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

    private static byte[] Jpeg(int length) => Picture(length, 0xFF, 0xD8, 0xFF);

    private static byte[] Gif(int length) => Picture(length, (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a');

    private async Task<(ApiTestHost Host, FakeBluesky Bluesky, string Cookie)> StartAsync(
        bool setUp = true, ModbotPermissions permissions = Everything)
    {
        await ApiTestHost.ResetDeploymentAsync(db, Ct);

        await using (var context = db.NewContext())
            await context.Posts.ExecuteDeleteAsync(Ct);

        ApiTestHost? started = null;
        var bluesky = new FakeBluesky(() => started?.Clock.UtcNow ?? DateTimeOffset.UnixEpoch)
        {
            ProfileRecord = Baseline(),
        };

        started = await ApiTestHost.StartAsync(db, configure: services =>
            services.AddHttpClient(BlueskyClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bluesky));

        // The one who sets Bluesky up is not the one under test.
        var (_, admin) = await started.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        if (setUp)
        {
            var saved = await started.SendJsonAsync(
                HttpMethod.Put, SettingsPath, new { handle = "@" + FakeBluesky.Handle, appPassword = FakeBluesky.AppPassword }, admin, Ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var check = await started.SendJsonAsync(HttpMethod.Post, SettingsPath + "/check", null, admin, Ct);
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        var (_, cookie) = await started.SignedInAsync(permissions, Ct);
        return (started, bluesky, cookie);
    }

    private static async Task<HttpResponseMessage> SaveAsync(
        ApiTestHost host, string cookie, string? name = null, string? bio = null, byte[]? avatar = null, byte[]? banner = null)
    {
        var form = new MultipartFormDataContent();

        if (name is not null)
            form.Add(new StringContent(name), "displayName");

        if (bio is not null)
            form.Add(new StringContent(bio), "bio");

        if (avatar is not null)
            form.Add(File(avatar), "avatar", "avatar");

        if (banner is not null)
            form.Add(File(banner), "banner", "banner");

        var request = host.Authenticated(HttpMethod.Put, Path, cookie);
        request.Content = form;
        return await host.Client.SendAsync(request, Ct);
    }

    private static ByteArrayContent File(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task<JsonElement> ReadAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<JsonElement> HealthAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/posts/health", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response) =>
        (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString();

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    /// <summary>Every field of <paramref name="before"/> is in <paramref name="after"/> as it was, except those named; nothing else was added.</summary>
    private static void AssertOnlyTheseChanged(JsonObject before, JsonObject? after, params string[] changed)
    {
        Assert.NotNull(after);

        foreach (var (key, value) in before)
        {
            if (changed.Contains(key))
                continue;

            Assert.True(after.ContainsKey(key), $"{key} was dropped");
            Assert.True(JsonNode.DeepEquals(value, after[key]), $"{key} was changed");
        }

        foreach (var (key, _) in after)
            Assert.True(before.ContainsKey(key) || changed.Contains(key), $"{key} was added");
    }

    private static void AssertNothingWasWritten(FakeBluesky bluesky)
    {
        Assert.Equal(0, bluesky.CallsTo(PutRecord));
        Assert.Equal(0, bluesky.CallsTo(UploadBlob));
        Assert.Null(bluesky.LastProfilePut);
    }

    // --- Reading ---

    [Fact]
    public async Task ThePageShowsTheProfileAsBlueskyShowsIt()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var view = await ReadAsync(host, cookie);

        Assert.Equal("ready", view.GetProperty("state").GetString());
        Assert.Equal(FakeBluesky.Handle, view.GetProperty("handle").GetString());
        Assert.Equal("Our group", view.GetProperty("displayName").GetString());
        Assert.Equal("We run the group.", view.GetProperty("bio").GetString());
        Assert.Contains("bafkrei-avatar", view.GetProperty("avatarUrl").GetString(), StringComparison.Ordinal);
        Assert.Contains("bafkrei-banner", view.GetProperty("bannerUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(120, view.GetProperty("followers").GetInt32());
        Assert.Equal(7, view.GetProperty("following").GetInt32());
        Assert.Equal(45, view.GetProperty("posts").GetInt32());
        Assert.True(view.GetProperty("canEdit").GetBoolean());
        Assert.True(view.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("limitedUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("saveError").ValueKind);
    }

    [Fact]
    public async Task APageOpenedAgainWithinTenSecondsAsksBlueskyNothing_AndAfterTenSecondsAsksAgain()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        await ReadAsync(host, cookie);
        host.Clock.Advance(TimeSpan.FromSeconds(9));
        await ReadAsync(host, cookie);
        Assert.Equal(1, bluesky.CallsTo(GetProfile));

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        bluesky.PublicProfile["followersCount"] = 121;
        var view = await ReadAsync(host, cookie);

        Assert.Equal(2, bluesky.CallsTo(GetProfile));
        Assert.Equal(121, view.GetProperty("followers").GetInt32());
    }

    [Fact]
    public async Task WithNoAccountThePageSaysNotSetUp_AndBlueskyIsAskedNothing()
    {
        var (host, bluesky, cookie) = await StartAsync(setUp: false);
        await using var running = host;

        var view = await ReadAsync(host, cookie);

        Assert.Equal("notSetUp", view.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("handle").ValueKind);
        Assert.False(view.GetProperty("signedIn").GetBoolean());
        Assert.Empty(bluesky.Requests);
    }

    [Fact]
    public async Task AProfileThatCannotBeReadIsUnavailableWithTheReason()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.PublicProfileStatus = HttpStatusCode.InternalServerError;

        var view = await ReadAsync(host, cookie);

        Assert.Equal("unavailable", view.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("problem").GetString()));
        Assert.Equal(FakeBluesky.Handle, view.GetProperty("handle").GetString());
    }

    [Fact]
    public async Task ARateLimitOnReadingStopsTheReading_AndIsNotTriedAgain()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.PublicProfileLimitedUntil = host.Clock.UtcNow.AddMinutes(30);

        var first = await ReadAsync(host, cookie);

        Assert.Equal("unavailable", first.GetProperty("state").GetString());
        Assert.Equal(BlueskyErrors.Limited, first.GetProperty("problem").GetString());
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("limitedUntil").ValueKind);
        Assert.NotNull((await SettingsAsync()).BlueskyProfileStoppedUntil);

        // Well past the ten seconds, still inside the stop: nothing goes to Bluesky.
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await ReadAsync(host, cookie);

        Assert.Equal("unavailable", second.GetProperty("state").GetString());
        Assert.Equal(1, bluesky.CallsTo(GetProfile));

        // The stop ends at the time Bluesky gave, and the page reads again.
        host.Clock.Advance(TimeSpan.FromMinutes(30));
        bluesky.PublicProfileLimitedUntil = null;
        var third = await ReadAsync(host, cookie);

        Assert.Equal("ready", third.GetProperty("state").GetString());
        Assert.Equal(2, bluesky.CallsTo(GetProfile));
    }

    [Fact]
    public async Task APersonWithoutManageSettingsReadsThePageButCannotEditIt()
    {
        var (host, bluesky, _) = await StartAsync();
        await using var running = host;
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var view = await ReadAsync(host, viewer);
        Assert.False(view.GetProperty("canEdit").GetBoolean());

        var save = await SaveAsync(host, viewer, name: "Sneaky");
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        AssertNothingWasWritten(bluesky);
    }

    [Fact]
    public async Task APersonWithoutViewAnalyticsCannotReadThePage()
    {
        var (host, _, _) = await StartAsync();
        await using var running = host;
        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewPosts, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, nobody, Ct)).StatusCode);
    }

    // --- Saving: what is refused before Bluesky is asked ---

    [Fact]
    public async Task ASaveOfNothingIsRefused()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var empty = await SaveAsync(host, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(BlueskyProfileEndpoints.NothingToSave, await ErrorOfAsync(empty));

        var notAForm = await host.SendJsonAsync(HttpMethod.Put, Path, new { displayName = "Json" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notAForm.StatusCode);

        AssertNothingWasWritten(bluesky);
    }

    [Fact]
    public async Task ANameOfSixtyFiveCharactersIsRefused_AndSixtyFourIsSaved()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var tooLong = await SaveAsync(host, cookie, name: new string('a', 65));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(BlueskyProfileRecord.NameTooLong, await ErrorOfAsync(tooLong));
        AssertNothingWasWritten(bluesky);

        var fits = await SaveAsync(host, cookie, name: new string('a', 64));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal(new string('a', 64), bluesky.LastProfilePut!["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABioOfTwoHundredFiftySevenCharactersIsRefused_AndTwoHundredFiftySixIsSaved()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var tooLong = await SaveAsync(host, cookie, bio: new string('b', 257));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(BlueskyProfileRecord.BioTooLong, await ErrorOfAsync(tooLong));
        AssertNothingWasWritten(bluesky);

        var fits = await SaveAsync(host, cookie, bio: new string('b', 256));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal(new string('b', 256), bluesky.LastProfilePut!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("avatar")]
    [InlineData("banner")]
    public async Task APictureOverOneMegabyteIsRefusedWith413_AndExactlyOneMegabyteIsTaken(string field)
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var tooBig = field == "avatar"
            ? await SaveAsync(host, cookie, avatar: Png(1_000_001))
            : await SaveAsync(host, cookie, banner: Png(1_000_001));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal(BlueskyProfileRecord.PictureTooBig, await ErrorOfAsync(tooBig));
        AssertNothingWasWritten(bluesky);

        var fits = field == "avatar"
            ? await SaveAsync(host, cookie, avatar: Png(1_000_000))
            : await SaveAsync(host, cookie, banner: Png(1_000_000));

        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal(new[] { 1_000_000 }, bluesky.Blobs.ToArray());
    }

    [Fact]
    public async Task APictureThatIsNotAPngOrJpegIsRefused_WhateverItIsCalled()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var gif = await SaveAsync(host, cookie, avatar: Gif(500));
        Assert.Equal(HttpStatusCode.BadRequest, gif.StatusCode);
        Assert.Equal(BlueskyProfileRecord.PictureNotAccepted, await ErrorOfAsync(gif));

        var text = await SaveAsync(host, cookie, banner: Encoding.UTF8.GetBytes("<html>not a picture</html>"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

        AssertNothingWasWritten(bluesky);
    }

    [Fact]
    public async Task AnEmptyFileIsRefused()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var response = await SaveAsync(host, cookie, avatar: []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNothingWasWritten(bluesky);
    }

    [Fact]
    public async Task AGoodNameWithABadPictureSavesNothing()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var response = await SaveAsync(host, cookie, name: "Fine name", banner: Gif(500));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNothingWasWritten(bluesky);
    }

    [Fact]
    public async Task WithNoAccountASaveIsRefused_AndBlueskyIsAskedNothing()
    {
        var (host, bluesky, cookie) = await StartAsync(setUp: false);
        await using var running = host;

        var response = await SaveAsync(host, cookie, name: "Our group");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BlueskyProfileService.NotSetUp, await ErrorOfAsync(response));
        Assert.Empty(bluesky.Requests);
    }

    // --- Saving: what goes to Bluesky ---

    [Theory]
    [InlineData("name")]
    [InlineData("bio")]
    [InlineData("avatar")]
    [InlineData("banner")]
    public async Task EachFieldSavesAlone_AndTheRestOfTheRecordIncludingThePinStays(string field)
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        var before = Baseline();

        var response = field switch
        {
            "name" => await SaveAsync(host, cookie, name: "Another name"),
            "bio" => await SaveAsync(host, cookie, bio: "Another bio"),
            "avatar" => await SaveAsync(host, cookie, avatar: Png(2000)),
            _ => await SaveAsync(host, cookie, banner: Jpeg(3000)),
        };

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var key = field switch { "name" => "displayName", "bio" => "description", _ => field };
        AssertOnlyTheseChanged(before, bluesky.LastProfilePut, key);

        var written = bluesky.LastProfilePut!;
        Assert.Equal(1, bluesky.ProfilePuts);
        Assert.True(JsonNode.DeepEquals(before["pinnedPost"], written["pinnedPost"]));
        Assert.True(JsonNode.DeepEquals(before["pinnedPost"], bluesky.PinnedPost));

        // Only a picture that was sent is uploaded.
        Assert.Equal(field is "avatar" or "banner" ? 1 : 0, bluesky.CallsTo(UploadBlob));
        Assert.Equal(field is "avatar" or "banner" ? 1 : 0, bluesky.Blobs.Count);

        switch (field)
        {
            case "name": Assert.Equal("Another name", written["displayName"]!.GetValue<string>()); break;
            case "bio": Assert.Equal("Another bio", written["description"]!.GetValue<string>()); break;
            case "avatar": Assert.Equal("bafkrei-blob-2000", written["avatar"]!["ref"]!["$link"]!.GetValue<string>()); break;
            default: Assert.Equal("bafkrei-blob-3000", written["banner"]!["ref"]!["$link"]!.GetValue<string>()); break;
        }
    }

    [Fact]
    public async Task AllFourSaveTogether_AndThePageShowsThemAtOnce()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await ReadAsync(host, cookie);

        var response = await SaveAsync(host, cookie, name: "New name", bio: "New bio", avatar: Png(2000), banner: Jpeg(3000));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal("New name", view.GetProperty("displayName").GetString());
        Assert.Equal("New bio", view.GetProperty("bio").GetString());
        Assert.Contains("bafkrei-blob-2000", view.GetProperty("avatarUrl").GetString(), StringComparison.Ordinal);
        Assert.Contains("bafkrei-blob-3000", view.GetProperty("bannerUrl").GetString(), StringComparison.Ordinal);

        // The numbers are the ones last read.
        Assert.Equal(120, view.GetProperty("followers").GetInt32());

        AssertOnlyTheseChanged(Baseline(), bluesky.LastProfilePut, "displayName", "description", "avatar", "banner");
        Assert.Equal(1, bluesky.ProfilePuts);

        // The record was read, then written with the content id that was read.
        var put = JsonNode.Parse(bluesky.Requests.Single(r => r.Method == PutRecord).Body!)!;
        Assert.Equal("bafy-profile", put["swapRecord"]!.GetValue<string>());
        Assert.Equal("self", put["rkey"]!.GetValue<string>());
        Assert.Equal(FakeBluesky.Did, put["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnEmptyNameAndAnEmptyBioClearThem_AndTheRestStays()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await ReadAsync(host, cookie);

        var response = await SaveAsync(host, cookie, name: string.Empty, bio: string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(bluesky.LastProfilePut!.ContainsKey("displayName"));
        Assert.False(bluesky.LastProfilePut.ContainsKey("description"));
        AssertOnlyTheseChanged(Baseline(), bluesky.LastProfilePut, "displayName", "description");

        var view = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("bio").ValueKind);
    }

    [Fact]
    public async Task TwoSavesInARowEachKeepWhatTheOtherChanged()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, name: "First name")).StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, bio: "Second bio")).StatusCode);

        var written = bluesky.LastProfilePut!;
        Assert.Equal("First name", written["displayName"]!.GetValue<string>());
        Assert.Equal("Second bio", written["description"]!.GetValue<string>());
        Assert.NotNull(written["pinnedPost"]);
        Assert.Equal(2, bluesky.ProfilePuts);
    }

    [Fact]
    public async Task ASecondSaveWithinTenSecondsIsRefusedWith429_AndBlueskyIsAskedOnce()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, name: "First name")).StatusCode);

        host.Clock.Advance(TimeSpan.FromSeconds(9));
        var tooSoon = await SaveAsync(host, cookie, name: "Second name");

        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Equal(BlueskyProfileService.SaveTooSoon, await ErrorOfAsync(tooSoon));
        Assert.Equal(1, bluesky.CallsTo(PutRecord));

        // Waiting too short is not a failure of Bluesky's: nothing is kept for Health.
        Assert.Equal(JsonValueKind.Null, (await HealthAsync(host, cookie)).GetProperty("profileError").ValueKind);

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, name: "Second name")).StatusCode);
        Assert.Equal(2, bluesky.CallsTo(PutRecord));
    }

    // --- Saving: Bluesky says no ---

    [Fact]
    public async Task ARateLimitOnTheUploadStopsTheLane_AndNothingIsTriedAgain()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.UploadLimitedUntil = host.Clock.UtcNow.AddMinutes(30);
        var readsBefore = bluesky.CallsTo(GetRecord);

        var first = await SaveAsync(host, cookie, avatar: Png(2000));

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(BlueskyErrors.Limited, await ErrorOfAsync(first));
        Assert.Equal(1, bluesky.CallsTo(UploadBlob));
        Assert.Equal(0, bluesky.CallsTo(PutRecord));
        Assert.NotNull((await SettingsAsync()).BlueskyStoppedUntil);

        // Past the ten seconds between saves and still inside the stop: not one more call.
        var callsAfterFirst = bluesky.Requests.Count;
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await SaveAsync(host, cookie, avatar: Png(2000));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(callsAfterFirst, bluesky.Requests.Count);
        Assert.Equal(1, bluesky.CallsTo(UploadBlob));
        Assert.Equal(readsBefore + 1, bluesky.CallsTo(GetRecord));

        var view = await ReadAsync(host, cookie);
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("limitedUntil").ValueKind);
        Assert.Equal(BlueskyErrors.Limited, view.GetProperty("saveError").GetString());
    }

    [Fact]
    public async Task ARateLimitOnTheWriteStopsTheLane_AndTheWriteIsMadeOnce()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.ProfilePutLimitedUntil = host.Clock.UtcNow.AddMinutes(30);

        var first = await SaveAsync(host, cookie, name: "New name");

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(BlueskyErrors.Limited, await ErrorOfAsync(first));
        Assert.Equal(1, bluesky.CallsTo(PutRecord));
        Assert.Equal(0, bluesky.ProfilePuts);

        var callsAfterFirst = bluesky.Requests.Count;
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        bluesky.ProfilePutLimitedUntil = null;
        var second = await SaveAsync(host, cookie, name: "New name");

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(callsAfterFirst, bluesky.Requests.Count);
        Assert.Equal(1, bluesky.CallsTo(PutRecord));

        // Once the stop has run out, a save goes through.
        host.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, name: "New name")).StatusCode);
        Assert.Equal(1, bluesky.ProfilePuts);
    }

    [Fact]
    public async Task AProfileSavedOnBlueskyMeanwhileIsNotWrittenOver_AndAnswers409()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.ProfileChangesAfterNextRead = true;

        var response = await SaveAsync(host, cookie, name: "New name");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BlueskyProfileRecord.ChangedMeanwhile, await ErrorOfAsync(response));
        Assert.Equal(0, bluesky.ProfilePuts);
        Assert.Null(bluesky.LastProfilePut);

        var view = await ReadAsync(host, cookie);
        Assert.Equal(BlueskyProfileRecord.ChangedMeanwhile, view.GetProperty("saveError").GetString());

        // Trying again reads the profile as it is now and goes through.
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, name: "New name")).StatusCode);
        Assert.Equal(1, bluesky.ProfilePuts);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(host, cookie)).GetProperty("saveError").ValueKind);
    }

    [Fact]
    public async Task AFailedSaveShowsOnThePageAndOnHealth_AndALaterSuccessClearsBoth()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.ProfilePutStatus = HttpStatusCode.Forbidden;

        var failed = await SaveAsync(host, cookie, name: "New name");

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var error = await ErrorOfAsync(failed);
        Assert.False(string.IsNullOrWhiteSpace(error));

        var view = await ReadAsync(host, cookie);
        Assert.Equal(error, view.GetProperty("saveError").GetString());
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("saveErrorAt").ValueKind);

        var health = await HealthAsync(host, cookie);
        Assert.Equal(error, health.GetProperty("profileError").GetString());
        Assert.NotEqual(JsonValueKind.Null, health.GetProperty("profileErrorAt").ValueKind);

        bluesky.ProfilePutStatus = null;
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, cookie, name: "New name")).StatusCode);

        var after = await ReadAsync(host, cookie);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("saveError").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("saveErrorAt").ValueKind);

        var healthAfter = await HealthAsync(host, cookie);
        Assert.Equal(JsonValueKind.Null, healthAfter.GetProperty("profileError").ValueKind);
        Assert.Equal(JsonValueKind.Null, healthAfter.GetProperty("profileErrorAt").ValueKind);
    }

    [Fact]
    public async Task ARefusalOfTheProfileRightIsKeptButTheFailureIsNotARateLimit()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.ProfilePutStatus = HttpStatusCode.Forbidden;

        await SaveAsync(host, cookie, name: "New name");

        Assert.Null((await SettingsAsync()).BlueskyStoppedUntil);
        Assert.NotNull((await SettingsAsync()).BlueskyProfileError);
    }

    // --- The error goes with the account ---

    [Fact]
    public async Task AChangedHandleClearsTheSaveError()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.ProfilePutStatus = HttpStatusCode.Forbidden;
        await SaveAsync(host, cookie, name: "New name");
        Assert.NotNull((await SettingsAsync()).BlueskyProfileError);

        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var changed = await host.SendJsonAsync(HttpMethod.Put, SettingsPath, new { handle = "another.bsky.social" }, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var settings = await SettingsAsync();
        Assert.Null(settings.BlueskyProfileError);
        Assert.Null(settings.BlueskyProfileErrorAt);
        Assert.Equal(JsonValueKind.Null, (await HealthAsync(host, cookie)).GetProperty("profileError").ValueKind);
    }

    [Fact]
    public async Task RemovingTheAccountClearsTheSaveError_ButKeepsTheStopOnReading()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        // A save that failed, and a reading that was limited.
        bluesky.ProfilePutStatus = HttpStatusCode.Forbidden;
        await SaveAsync(host, cookie, name: "New name");
        bluesky.PublicProfileLimitedUntil = host.Clock.UtcNow.AddHours(2);
        await ReadAsync(host, cookie);

        var before = await SettingsAsync();
        Assert.NotNull(before.BlueskyProfileError);
        Assert.NotNull(before.BlueskyProfileStoppedUntil);

        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var removed = await host.SendJsonAsync(HttpMethod.Delete, SettingsPath, null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var after = await SettingsAsync();
        Assert.Null(after.BlueskyProfileError);
        Assert.Null(after.BlueskyProfileErrorAt);

        // The public API's limit is on Modbot's address, not on the account: removing and adding an account
        // is no way round it.
        Assert.Equal(before.BlueskyProfileStoppedUntil, after.BlueskyProfileStoppedUntil);
    }

    // --- The audit log ---

    private async Task<List<JsonElement>> ProfileChangesAsync(ApiTestHost host)
    {
        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);

        return facts.Select(ApiTestHost.DataOf)
            .Where(d => d.GetProperty("setting").GetString() == "bluesky"
                && d.GetProperty("changed").EnumerateObject().Any(p => p.Name.StartsWith("profile", StringComparison.Ordinal)))
            .Select(d => d.GetProperty("changed"))
            .ToList();
    }

    [Fact]
    public async Task TheAuditEntryListsOnlyTheFieldsThatChanged()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        await SaveAsync(host, cookie, name: "New name");

        var changes = await ProfileChangesAsync(host);
        var entry = Assert.Single(changes);
        Assert.Equal(new[] { "profileName" }, entry.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Our group", entry.GetProperty("profileName").GetProperty("old").GetString());
        Assert.Equal("New name", entry.GetProperty("profileName").GetProperty("new").GetString());
    }

    [Fact]
    public async Task APictureIsAuditedByItsContentId_AndSeveralFieldsShareOneEntry()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        await SaveAsync(host, cookie, bio: "New bio", avatar: Png(2000), banner: Jpeg(3000));

        var entry = Assert.Single(await ProfileChangesAsync(host));
        var names = entry.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "profileBanner", "profileBio", "profilePicture" }, names);
        Assert.Equal("bafkrei-old-avatar", entry.GetProperty("profilePicture").GetProperty("old").GetString());
        Assert.Equal("bafkrei-blob-2000", entry.GetProperty("profilePicture").GetProperty("new").GetString());
        Assert.Equal("bafkrei-blob-3000", entry.GetProperty("profileBanner").GetProperty("new").GetString());
    }

    [Fact]
    public async Task ASaveThatChangesNothingWritesNoAuditEntry()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var response = await SaveAsync(host, cookie, name: "Our group", bio: "We run the group.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ProfileChangesAsync(host));
    }

    [Fact]
    public async Task AFailedSaveWritesNoAuditEntry()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.ProfilePutStatus = HttpStatusCode.Forbidden;

        await SaveAsync(host, cookie, name: "New name");

        Assert.Empty(await ProfileChangesAsync(host));
    }

    // --- The Posts tab ---

    private async Task AddPostAsync(
        string title, DateTimeOffset postedAt, string network = PostNetworks.Bluesky, string target = FakeBluesky.Did,
        string state = PostDestinationStates.Posted, int? likes = null)
    {
        var key = Guid.NewGuid().ToString("N")[..13];
        var now = postedAt;

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Text = "Text of " + title,
            Status = PostStatuses.Scheduled,
            SendAt = postedAt,
            TimeZone = "UTC",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now,
        };

        post.Destinations.Add(new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = network,
            Target = target,
            State = state,
            ClientKey = key,
            ExternalId = state == PostDestinationStates.Posted ? BlueskyText.PostUri(FakeBluesky.Did, key) : null,
            Link = state == PostDestinationStates.Posted ? BlueskyText.PostLink(FakeBluesky.Did, key) : null,
            SentTitle = title,
            SentText = "Sent text of " + title,
            PostedAt = state == PostDestinationStates.Posted ? postedAt : null,
            LikeCount = likes,
            RepostCount = likes is null ? null : 2,
            ReplyCount = likes is null ? null : 1,
            CountedAt = likes is null ? null : postedAt.AddHours(1),
            UpdatedAt = now,
        });

        await using var context = db.NewContext();
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> PostsAsync(ApiTestHost host, string cookie, int? page = null)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, Path + "/posts" + (page is null ? string.Empty : "?page=" + page), null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    [Fact]
    public async Task ThePostsTabListsOnlyWhatModbotPostedToTheAccount_NewestFirst_TwentyToAPage()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 25; i++)
            await AddPostAsync($"Mine {i:00}", start.AddHours(i));

        // None of these belong on the tab.
        await AddPostAsync("On Discord", start.AddDays(10), network: PostNetworks.Discord);
        await AddPostAsync("Other account", start.AddDays(10), target: "did:plc:somebodyelse");
        await AddPostAsync("Not sent yet", start.AddDays(10), state: PostDestinationStates.Waiting);
        await AddPostAsync("Failed", start.AddDays(10), state: PostDestinationStates.Failed);
        await AddPostAsync("Taken down", start.AddDays(10), state: PostDestinationStates.Removed);

        var first = await PostsAsync(host, cookie);

        Assert.Equal(25, first.GetProperty("total").GetInt32());
        Assert.Equal(20, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        var onFirst = first.GetProperty("posts").EnumerateArray().ToList();
        Assert.Equal(20, onFirst.Count);
        Assert.Equal("Sent text of Mine 24", onFirst[0].GetProperty("text").GetString());
        Assert.Equal("Sent text of Mine 05", onFirst[^1].GetProperty("text").GetString());
        Assert.DoesNotContain(onFirst, p => p.GetProperty("title").GetString() is "On Discord" or "Other account" or "Not sent yet" or "Failed" or "Taken down");

        var second = await PostsAsync(host, cookie, page: 2);
        var onSecond = second.GetProperty("posts").EnumerateArray().ToList();
        Assert.Equal(5, onSecond.Count);
        Assert.Equal("Sent text of Mine 04", onSecond[0].GetProperty("text").GetString());
        Assert.Equal("Sent text of Mine 00", onSecond[^1].GetProperty("text").GetString());
        Assert.Equal(2, second.GetProperty("page").GetInt32());

        var beyond = await PostsAsync(host, cookie, page: 3);
        Assert.Empty(beyond.GetProperty("posts").EnumerateArray());
        Assert.Equal(25, beyond.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task APostShowsItsNumbersOnceCounted_AndNullBeforeTheFirstCount()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        await AddPostAsync("Counted", start.AddHours(1), likes: 7);
        await AddPostAsync("Not counted", start);

        var posts = (await PostsAsync(host, cookie)).GetProperty("posts").EnumerateArray().ToList();

        var counted = posts.Single(p => p.GetProperty("title").GetString() == "Counted").GetProperty("counts");
        Assert.Equal(7, counted.GetProperty("likes").GetInt32());
        Assert.Equal(2, counted.GetProperty("reposts").GetInt32());
        Assert.Equal(1, counted.GetProperty("replies").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, counted.GetProperty("countedAt").ValueKind);

        var notCounted = posts.Single(p => p.GetProperty("title").GetString() == "Not counted");
        Assert.Equal(JsonValueKind.Null, notCounted.GetProperty("counts").ValueKind);
        Assert.False(string.IsNullOrEmpty(notCounted.GetProperty("link").GetString()));
    }

    [Fact]
    public async Task ThePostsTabIsEmptyWithNoAccount()
    {
        var (host, _, cookie) = await StartAsync(setUp: false);
        await using var running = host;
        await AddPostAsync("Mine", DateTimeOffset.UnixEpoch.AddDays(30));

        var tab = await PostsAsync(host, cookie);

        Assert.Empty(tab.GetProperty("posts").EnumerateArray());
        Assert.Equal(0, tab.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ThePostsTabNeedsViewPosts()
    {
        var (host, _, _) = await StartAsync();
        await using var running = host;
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path + "/posts", null, viewer, Ct)).StatusCode);
    }
}
