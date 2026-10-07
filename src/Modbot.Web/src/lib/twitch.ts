// Relative, with the extension, so the Node test runner loads it as it is (see lib/nav.ts).
import type { TwitchPostPlaces } from './api.ts'

/**
 * The words in the "We're live on Twitch" post's title and text that Modbot fills in when the post
 * is made: the stream's title, its category, and the channel's link. A button for each puts the
 * word in; nothing else explains them (CLAUDE.md).
 */
export const TWITCH_WORDS: readonly { word: string; label: string }[] = [
  { word: '{title}', label: 'Stream title' },
  { word: '{category}', label: 'Category' },
  { word: '{link}', label: 'Link' },
]

/** Whether two sets of ticked sites are the same, a role list left out and an empty one counting alike. */
export function placesEqual(a: TwitchPostPlaces, b: TwitchPostPlaces): boolean {
  if (a.bluesky !== b.bluesky) return false
  if (!a.discord !== !b.discord) return false
  if (!a.vrChat !== !b.vrChat) return false

  if (a.discord && b.discord) {
    if ((a.discord.channelId ?? '') !== (b.discord.channelId ?? '')) return false
    if ((a.discord.roleId ?? '') !== (b.discord.roleId ?? '')) return false
    if (a.discord.publish !== b.discord.publish) return false
  }

  if (a.vrChat && b.vrChat) {
    if (a.vrChat.visibility !== b.vrChat.visibility) return false
    if (a.vrChat.notify !== b.vrChat.notify) return false

    const rolesA = [...(a.vrChat.roleIds ?? [])].sort()
    const rolesB = [...(b.vrChat.roleIds ?? [])].sort()
    if (rolesA.length !== rolesB.length || rolesA.some((r, i) => r !== rolesB[i])) return false
  }

  return true
}

/** The sites ticked, by name, in the order the settings list them. */
export function tickedSites(places: TwitchPostPlaces): string[] {
  return [places.discord ? 'Discord' : null, places.vrChat ? 'VRChat' : null, places.bluesky ? 'Bluesky' : null].filter(
    (s): s is string => s !== null,
  )
}

/**
 * How long a stream has been live, as the card says it: "12 min", "1 h 5 min". Under a minute is
 * "Just now". `now` is passed in so the card can tick and a test can fix it.
 */
export function liveFor(startedAt: string, now: number): string {
  const minutes = Math.max(0, Math.floor((now - Date.parse(startedAt)) / 60_000))
  if (minutes < 1) return 'Just now'
  if (minutes < 60) return `${minutes} min`

  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`
}
