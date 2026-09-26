using System.Text.Json;
using Modbot.Core.Cloud;
using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Cloud;

/// <summary>
/// What a Modbot server may tell Modbot Cloud, and the link code it shows its owner
/// (Cloud accounts and registry spec 3).
/// </summary>
public class ServerReportTests
{
    /// <summary>
    /// The promise about counts is the shape of the type, not a rule somebody has to remember on
    /// every future edit (central services spec 4.6).
    /// </summary>
    [Fact]
    public void A_report_has_no_field_for_any_count_of_people()
    {
        var fields = typeof(ServerReport).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(fields, name => name.Contains("Member", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("User", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Scale", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Bucket", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("PairedClients", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_report_is_written_in_the_field_names_cloud_reads()
    {
        var json = JsonSerializer.Serialize(new ServerReport
        {
            GroupId = "grp_1",
            GroupName = "VRChat Kings",
            RateLimitColdStops = 3,
        });

        Assert.Contains("\"groupId\":\"grp_1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"groupName\":\"VRChat Kings\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rateLimitColdStops\":3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_code_is_eight_readable_characters()
    {
        var codes = Enumerable.Range(0, 50)
            .Select(_ => System.Security.Cryptography.RandomNumberGenerator.GetString(
                ServerReporter.LinkCodeAlphabet, ServerReporter.LinkCodeLength))
            .ToList();

        Assert.All(codes, code =>
        {
            Assert.Equal(ServerReporter.LinkCodeLength, code.Length);
            Assert.All(code, c => Assert.Contains(c, ServerReporter.LinkCodeAlphabet));
        });

        // Nothing that reads as a one or a zero.
        Assert.DoesNotContain('I', ServerReporter.LinkCodeAlphabet);
        Assert.DoesNotContain('L', ServerReporter.LinkCodeAlphabet);
        Assert.DoesNotContain('O', ServerReporter.LinkCodeAlphabet);
        Assert.DoesNotContain('U', ServerReporter.LinkCodeAlphabet);

        // Not all the same: a code that repeated would claim the wrong server.
        Assert.True(codes.Distinct().Count() > 45);
    }

    [Fact]
    public void A_link_code_is_hashed_the_way_cloud_compares_it()
    {
        // Lower-case hex SHA-256, 64 characters. Cloud refuses anything else.
        var hash = ServerReporter.HashCode("A1B2C3D4");

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        Assert.Equal(hash, ServerReporter.HashCode("A1B2C3D4"));
        Assert.NotEqual(hash, ServerReporter.HashCode("A1B2C3D5"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not json", null)]
    [InlineData("""{"Name":"VRChat Kings"}""", null)]
    [InlineData("""{"Description":"A group."}""", "A group.")]
    public void The_group_description_is_read_out_of_the_stored_snapshot(string? json, string? expected) =>
        Assert.Equal(expected, ServerReporter.GroupDescription(json));

    [Fact]
    public void The_reporting_schedule_is_six_hours_with_an_hour_between_retries()
    {
        Assert.Equal(TimeSpan.FromHours(6), ServerReportingService.ReportInterval);
        Assert.Equal(TimeSpan.FromHours(1), ServerReportingService.RetryDelay);

        // Start-up is not the moment to make a network call.
        Assert.True(ServerReportingService.FirstDelay >= TimeSpan.FromMinutes(1));
    }
}

/// <summary>
/// MODBOT_MY_URL: where this server points people for the server selector.
/// </summary>
public class MyUrlTests
{
    [Fact]
    public void Unset_means_the_project_own_selector()
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>());

        Assert.Equal("https://my.modbot.co", environment.MyUrl);
    }

    [Theory]
    [InlineData("https://my.example.org", "https://my.example.org")]
    [InlineData("https://my.example.org/", "https://my.example.org")]
    [InlineData("https://my.example.org/selector/", "https://my.example.org/selector")]
    [InlineData("my.example.org", "https://my.modbot.co")]
    [InlineData("ftp://my.example.org", "https://my.modbot.co")]
    [InlineData("  ", "https://my.modbot.co")]
    public void An_address_is_kept_without_its_trailing_slash_or_ignored(string set, string expected)
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_MY_URL"] = set,
        });

        Assert.Equal(expected, environment.MyUrl);
    }
}
