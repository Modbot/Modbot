namespace Modbot.Discord;

/// <summary>
/// The address of the Modbot mark on this deployment, for the small icon beside an embed's footer.
/// Built from the public address setting the way <see cref="PersonLink"/> is, and null without one.
/// </summary>
public static class BrandIcon
{
    public static string? For(string? publicAddress)
        => string.IsNullOrWhiteSpace(publicAddress) ? null : $"{publicAddress.TrimEnd('/')}/icon-192.png";
}
