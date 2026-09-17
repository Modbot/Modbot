/**
 * What an instance is called on a screen: the world's name and VRChat's number, the way VRChat
 * shows it in game — `The Black Cat #19453`.
 *
 * The number is the part after the colon in the instance id. A world Modbot has not read the page
 * of yet has no name, so its id stands in: `wrld_4cf5… #19453` is still the real answer, and
 * "Unnamed world" would be a claim nobody checked. With no number either, the instance is just
 * "an instance", which is what the sentence says while it has nothing better.
 */
export function instanceName(worldName: string | null | undefined, worldId: string | null | undefined, number: string | null | undefined): string {
  const world = worldName || worldId || null
  const tag = number ? `#${number}` : null

  if (world && tag) return `${world} ${tag}`
  if (world) return world
  if (tag) return `instance ${tag}`
  return 'an instance'
}

/** `#19453` on its own, where the world is already named beside it. */
export function instanceNumber(number: string | null | undefined): string {
  return number ? `#${number}` : 'this instance'
}
