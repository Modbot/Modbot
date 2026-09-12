namespace Modbot.Core.Data.Entities;

/// <summary>The auto-generated encryption key. Singleton, like <see cref="Settings"/>.</summary>
public class ProtectorKey
{
    public int Id { get; set; } = 1;
    public byte[] Key { get; set; } = [];
}
