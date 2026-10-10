using System.Runtime.Serialization;
using Newtonsoft.Json;
using VRChat.API.Model;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// The body of a VRChat calendar update, with the access type the SDK's own update model leaves out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every update was refused</strong> (seen 2026-10-01, on master as well as here): VRChat
/// answered 400, "Can't unpublish or change access type once the calendar entry is published", to a
/// one-date move and to a description edit alike. The SDK's <see cref="UpdateCalendarEventRequest"/>
/// has no <c>accessType</c> at all, so the body went without one -- checked by capturing the SDK's
/// own request on this machine -- and VRChat took the missing value as a change of who can see the
/// event. The update now carries the same <c>accessType</c> the create sent.
/// </para>
/// <para>
/// <c>isDraft</c> stays <c>false</c>, exactly as the create sends it: an update never asks for a
/// draft. The SDK always writes it, and a subclass cannot take a property of the base model out of
/// the body.
/// </para>
/// </remarks>
[DataContract(Name = "UpdateCalendarEventRequest")]
public sealed class CalendarUpdateBody : UpdateCalendarEventRequest
{
    public CalendarUpdateBody()
    {
    }

    /// <summary><c>group</c> or <c>public</c>: who sees the event on VRChat, exactly as the create sent it.</summary>
    [DataMember(Name = "accessType", EmitDefaultValue = false)]
    [JsonProperty("accessType", NullValueHandling = NullValueHandling.Ignore)]
    public string? AccessType { get; set; }

    /// <summary>
    /// Featured, or nothing at all. The SDK's own <c>featured</c> is a plain <c>bool</c> that is
    /// always written, as false when never set, and VRChat refuses an account that may not feature
    /// as soon as it says "featured" with true -- even to repeat what VRChat already holds, as for
    /// an event made there (seen 2026-10-09). This hides it, so null leaves the field out of the
    /// body and VRChat keeps what it has. Checked by printing the body: null is left out, and true
    /// and false are written once.
    /// </summary>
    [DataMember(Name = "featured", EmitDefaultValue = false)]
    [JsonProperty("featured", NullValueHandling = NullValueHandling.Ignore)]
    public new bool? Featured { get; set; }
}
