using System.Runtime.Serialization;

namespace Modbot.VRChat;

/// <summary>The word VRChat sends for a value the SDK generated as a C# enum member.</summary>
/// <remarks>
/// The generator renames values so they are legal C#: <c>18+</c> becomes <c>plus18</c>,
/// <c>emailOtp</c> becomes <c>EmailOtp</c>. So <c>ToString()</c> is not what VRChat said, and the
/// <c>EnumMember</c> attribute beside it is. Everything Modbot stores or shows a person goes
/// through here, so a column, a diff and an error message all spell a value the way VRChat's own
/// documentation spells it.
/// </remarks>
internal static class VRChatWords
{
    public static string? Of<T>(T value) where T : struct, Enum
    {
        var member = typeof(T).GetField(value.ToString());
        var wire = member?
            .GetCustomAttributes(typeof(EnumMemberAttribute), false)
            .OfType<EnumMemberAttribute>()
            .FirstOrDefault()?.Value;

        return wire ?? value.ToString();
    }
}
