using System.Net;
using Microsoft.Extensions.Primitives;
using Modbot.My.Common;

namespace Modbot.My.Tests;

/// <summary>
/// The real client address behind Cloudflare and Railway's edge, and the headers that must not be
/// believed.
/// </summary>
public class ClientAddressTests
{
    private const string Visitor = "198.51.100.9";
    private const string CloudflareEdge = "162.158.1.1";
    private const string Direct = "203.0.113.7";

    private static string? Resolve(StringValues forwardedFor, StringValues cloudflare = default, string? connection = null) =>
        ClientAddress.Resolve(forwardedFor, cloudflare, connection is null ? null : IPAddress.Parse(connection))?.ToString();

    [Fact]
    public void ThroughCloudflareTheVisitorComesFromCfConnectingIp()
    {
        Assert.Equal(Visitor, Resolve($"{Visitor}, {CloudflareEdge}", Visitor, "10.0.0.1"));
    }

    [Fact]
    public void ThroughCloudflareOverIpv6TheVisitorComesFromCfConnectingIp()
    {
        Assert.Equal("2001:db8::5", Resolve("2606:4700::1", "2001:db8::5"));
    }

    [Fact]
    public void WithoutCloudflareTheRightMostForwardedForEntryIsUsed()
    {
        Assert.Equal(Direct, Resolve($"1.1.1.1, {Direct}", connection: "10.0.0.1"));
    }

    [Fact]
    public void ACfConnectingIpSentStraightToTheOriginIsIgnored()
    {
        // Someone calling the Railway address directly: Railway's edge appends their own address.
        Assert.Equal(Direct, Resolve(Direct, Visitor));
    }

    [Fact]
    public void ACloudflareAddressTheClientWroteFurtherLeftDoesNotCount()
    {
        Assert.Equal(Direct, Resolve($"{CloudflareEdge}, {Direct}", Visitor));
    }

    [Fact]
    public void ForwardedForEntriesTheClientWroteAreIgnored()
    {
        Assert.Equal(Direct, Resolve($"{Visitor}, 6.6.6.6, {Direct}"));
    }

    [Fact]
    public void ACfConnectingIpWithNoForwardedForIsIgnored()
    {
        Assert.Equal("10.0.0.2", Resolve(StringValues.Empty, Visitor, "10.0.0.2"));
    }

    [Fact]
    public void WithNoForwardedForTheConnectionAddressIsUsed()
    {
        Assert.Equal("10.0.0.3", Resolve(StringValues.Empty, connection: "::ffff:10.0.0.3"));
        Assert.Null(Resolve(StringValues.Empty));
    }

    [Fact]
    public void TheLastOfSeveralForwardedForHeadersIsTheRightMostEntry()
    {
        Assert.Equal(Direct, Resolve(new StringValues([Visitor, $"6.6.6.6, {Direct}"])));
    }

    [Fact]
    public void AMalformedOrDoubledCfConnectingIpFallsBackToTheCloudflareEdge()
    {
        Assert.Equal(CloudflareEdge, Resolve(CloudflareEdge, "not-an-address"));
        Assert.Equal(CloudflareEdge, Resolve(CloudflareEdge, new StringValues([Visitor, "192.0.2.1"])));
    }

    [Fact]
    public void AMalformedRightMostEntryFallsBackToTheConnection()
    {
        Assert.Equal("10.0.0.4", Resolve($"{Direct}, unknown", Visitor, "10.0.0.4"));
    }

    [Fact]
    public void APortOnTheAddressIsDropped()
    {
        Assert.Equal(Direct, Resolve($"{Direct}:4711"));
        Assert.Equal("2001:db8::7", Resolve("[2001:db8::7]:4711"));
    }

    [Theory]
    [InlineData("173.245.48.1", true)]
    [InlineData("104.16.0.1", true)]
    [InlineData("2a06:98c0::1", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("10.0.0.1", false)]
    public void CloudflareRangesAreRecognised(string address, bool expected)
    {
        Assert.Equal(expected, ClientAddress.IsCloudflare(IPAddress.Parse(address)));
    }
}
