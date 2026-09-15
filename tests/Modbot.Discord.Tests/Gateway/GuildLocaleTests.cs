using System.Globalization;

namespace Modbot.Discord.Tests.Gateway;

/// <summary>
/// A Discord server's language must not break loading the server.
/// </summary>
/// <remarks>
/// Discord.Net turns the server's preferred language into a <see cref="CultureInfo"/> when the
/// server arrives (<c>SocketGuild.Update</c>). Modbot runs in invariant globalization mode, where
/// asking for any named culture throws by default, so the live bot logged
/// "Error handling Dispatch (GUILD_AVAILABLE)" on every start and never finished loading the
/// server's channels and roles. Test projects share the same build settings as the host, so this
/// fails if that setting is ever lost.
/// </remarks>
public class GuildLocaleTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("ja")]
    public void AServersLanguage_CanBeTurnedIntoACulture(string locale)
    {
        var culture = new CultureInfo(locale);

        Assert.Equal(locale, culture.Name);
    }
}
