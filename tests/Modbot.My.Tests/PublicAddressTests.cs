using System.Net;
using Modbot.My.Common;

namespace Modbot.My.Tests;

/// <summary>
/// The rule on the one request my.modbot.co makes to somewhere a stranger named
/// (register details spec 2.2).
/// </summary>
public class PublicAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    // Where cloud metadata services live, which is the whole reason this rule exists.
    [InlineData("169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    // The same private address written as IPv6 is the same address.
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("::ffff:169.254.169.254")]
    public void An_address_that_is_not_out_on_the_internet_is_refused(string address) =>
        Assert.False(PublicAddresses.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("104.16.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1")]
    public void An_ordinary_address_is_allowed(string address) =>
        Assert.True(PublicAddresses.IsPublic(IPAddress.Parse(address)));
}
