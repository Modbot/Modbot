using System.Net;
using Modbot.Api.Features.Mcp;

namespace Modbot.Api.Tests.Features.Mcp;

/// <summary>
/// The fetch of a stranger's client document may only reach the public internet: not this
/// machine, not the private network beside it, not the cloud metadata address.
/// </summary>
public class PublicAddressesTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    public void PrivateAndSpecialAddressesAreNotPublic(string address)
        => Assert.False(PublicAddresses.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    public void PublicAddressesArePublic(string address)
        => Assert.True(PublicAddresses.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task ALiteralPrivateHostResolvesToNothing()
    {
        Assert.Empty(await PublicAddresses.ResolvePublicAsync("10.0.0.5", TestContext.Current.CancellationToken));
        Assert.Empty(await PublicAddresses.ResolvePublicAsync("[::1]", TestContext.Current.CancellationToken));
        Assert.Single(await PublicAddresses.ResolvePublicAsync("1.1.1.1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OnlyANamedHttpsAddressOnTheUsualPortIsAClientId()
    {
        Assert.True(McpClientDocuments.IsDocumentAddress("https://claude.ai/.well-known/oauth/client-id.json", out _));
        Assert.False(McpClientDocuments.IsDocumentAddress("https://10.0.0.5/client.json", out _));
        Assert.False(McpClientDocuments.IsDocumentAddress("https://[::1]/client.json", out _));
        Assert.False(McpClientDocuments.IsDocumentAddress("https://claude.ai:8443/client.json", out _));
    }
}
