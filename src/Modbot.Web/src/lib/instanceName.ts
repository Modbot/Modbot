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
