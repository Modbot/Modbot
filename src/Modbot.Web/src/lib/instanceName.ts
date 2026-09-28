import { accessInGame, headCountText } from './format.ts'

/**
 * What an instance is called on a screen: the world's name and VRChat's number, the way VRChat
 * shows it in game — `The Black Cat #19453`.
 *
 * An instance opened with a name of its own is called by that name instead, in quotes the way the
 * sentences quote names: `Murder 4 “6 killed 7”`. The number is still what finds it in game, so
 * the screens that use this keep the number close by, in a tooltip or a field of its own.
 *
 * The number is the part after the colon in the instance id. A world Modbot has not read the page
 * of yet has no name, so its id stands in: `wrld_4cf5… #19453` is still the real answer, and
 * "Unnamed world" would be a claim nobody checked. With no number either, the instance is just
 * "an instance", which is what the sentence says while it has nothing better.
 */
export function instanceName(
  worldName: string | null | undefined,
  worldId: string | null | undefined,
  number: string | null | undefined,
  name?: string | null,
): string {
  const world = worldName || worldId || null
  const tag = instanceTag(number, name)

  if (world && tag) return `${world} ${tag}`
  if (world) return world
  if (tag) return `instance ${tag}`
  return 'an instance'
}

/** `#19453` on its own, where the world is already named beside it — or `“6 killed 7”` when it has a name. */
export function instanceNumber(number: string | null | undefined, name?: string | null): string {
  return instanceTag(number, name) ?? 'this instance'
}

/** The instance's name in quotes, or its number with a hash, or null with neither. A blank name is no name. */
function instanceTag(number: string | null | undefined, name: string | null | undefined): string | null {
  const named = name?.trim()
  if (named) return `“${named}”`
  return number ? `#${number}` : null
}

/**
 * The words an open instance is drawn with, shared by the big tile and the phone's one-row header
 * so the two never disagree: the world's name, "25/40", the game's word for the access type, and
 * the instance's number or its own name in quotes.
 *
 * The tile keeps the number in the popup and names only an instance opened with a name; the
 * header has a line for it, so it always shows the number. Each has a label for screen readers
 * that says what it shows.
 */
export function instanceCardText(i: {
  worldName: string | null
  instanceName?: string | null
  number?: string | null
  people: number | null
  peopleUnsure?: boolean
  capacity: number | null
  groupAccessType: string | null
  region: string | null
}) {
  const here = i.people ?? 0
  const unsure = i.people !== null && !!i.peopleUnsure
  const count = `${headCountText(here, unsure)}${i.capacity ? `/${i.capacity}` : ''}`
  const access = accessInGame(i.groupAccessType)
  const name = i.worldName ?? 'Unknown world'
  const tag = instanceTag(i.number, i.instanceName)
  const named = i.instanceName?.trim() ? tag : null
  const label = (shown: string | null) =>
    `${name}${shown ? ` ${shown}` : ''}, ${count}${access ? `, ${access}` : ''}${i.region ? `, ${i.region.toUpperCase()}` : ''}`

  return { here, unsure, count, access, name, tag, named, tileLabel: label(named), headerLabel: label(tag) }
}

/**
 * How an instance came to an end, in the one word a row or popup shows beside the time.
 *
 * `closed` only when a moderator closed it by hand, which is what VRChat's audit log and the
 * "Closed" tile count. Most instances just empty out and drop off the group's list, and those
 * `ended`. Calling every one of them closed put "closed" on rows under a tile that said none were.
 */
export function instanceEnd(instance: { closedAt: string | null; closedByModerator?: boolean }): 'open now' | 'closed' | 'ended' {
  if (!instance.closedAt) return 'open now'
  return instance.closedByModerator ? 'closed' : 'ended'
}
