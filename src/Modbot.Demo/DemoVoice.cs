namespace Modbot.Demo;

/// <param name="Left">Null while the person is still in the voice channel.</param>
public sealed record DemoVoiceSession(DemoPerson Person, DateTimeOffset Joined, DateTimeOffset? Left);

/// <summary>
/// Evenings in the Discord lounge's voice channel, for people who are still around.
/// </summary>
/// <remarks>
/// The Discord history writes these as voice facts, and the group's online count counts the people in
/// them (<see cref="DemoOnline"/>). Both draw them from here, so the two agree about who was talking
/// when.
/// </remarks>
public static class DemoVoice
{
    /// <summary>The seed of the Discord history's dice, which draws these first.</summary>
    public const int Seed = 77;

    /// <summary>
    /// The year's voice sessions, drawn from <paramref name="random"/>.
    /// </summary>
    /// <remarks>
    /// The Discord history goes on to draw its moderation from the same dice, so the draws here, and
    /// their order, are exactly the ones it has always made: change one and every Discord ban and
    /// timeout after it moves too. A caller that only wants the sessions passes a fresh
    /// <c>new Random(Seed)</c>.
    /// </remarks>
    public static List<DemoVoiceSession> Sessions(DemoPlan plan, Random random)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(random);

        var present = plan.People.Where(p => p.DiscordUserId is not null && p.LeftGroupAt is null).ToList();
        var sessions = new List<DemoVoiceSession>();

        for (var dayBack = DemoPlan.DaysOfHistory; dayBack >= 0; dayBack--)
        {
            var day = plan.Now.AddDays(-dayBack).Date;
            var count = random.Next(0, 7);

            for (var i = 0; i < count; i++)
            {
                var person = present[random.Next(present.Count)];

                if (person.JoinedGroupAt > plan.Now.AddDays(-dayBack))
                    continue;

                var joined = new DateTimeOffset(day.AddHours(19 + random.Next(0, 5)).AddMinutes(random.Next(0, 60)), TimeSpan.Zero);

                if (joined >= plan.Now)
                    continue;

                var left = joined.AddMinutes(random.Next(8, 180));

                sessions.Add(new DemoVoiceSession(person, joined, left < plan.Now ? left : null));
            }
        }

        return sessions;
    }
}
