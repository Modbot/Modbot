using Avalonia.Media.Imaging;

namespace Modbot.UiBoard;

/// <summary>
/// What draws each tile of <see cref="Names"/>, by the tile's id. One file per side adds its own
/// (<c>Surfaces.Vr.cs</c>, and so on); every tile in the list must have one.
/// </summary>
internal static partial class Surfaces
{
    public static IReadOnlyDictionary<string, Func<RenderTargetBitmap>> All()
    {
        var draws = new Dictionary<string, Func<RenderTargetBitmap>>();

        AddVr(draws);

        foreach (var tile in Names.Tiles)
        {
            if (!draws.ContainsKey(tile.Id))
                throw new InvalidOperationException($"The tile '{tile.Id}' is in Names.cs but nothing draws it.");
        }

        return draws;
    }
}
