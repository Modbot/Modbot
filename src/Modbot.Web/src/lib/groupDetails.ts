/**
 * A "group details changed" entry, said in plain words.
 *
 * Two things write these. The group-info poll writes one whenever something on the group moved,
 * with nobody named, and most of them are only the member count or the online count ticking. A
 * person's edit reaches the audit log with their name. The poll's are readings, and read as an
 * edit they say "Somebody changed the group's details" about nothing anyone did.
 */

type Pair = { old?: unknown; new?: unknown }

/**
 * The group's fields as its own page says them, by the name a payload writes them under. The poll
 * writes them capitalised and VRChat's audit log in camel case, so the lookup ignores case.
 */
const FIELD_NAMES: Record<string, string> = {
  name: 'name',
  shortcode: 'short code',
  discriminator: 'short code number',
  description: 'description',
  rules: 'rules',
  ownerid: 'owner',
  joinstate: 'join state',
  privacy: 'privacy',
  isverified: 'verified',
  membercount: 'members',
  onlinemembercount: 'members online',
  roles: 'roles',
}

/** The group field in plain words, lower case, or null when it is not one this knows. */
export function groupFieldName(key: string): string | null {
  return FIELD_NAMES[key.toLowerCase()] ?? null
}

/** The same, starting with a capital, for a label. Null when it is not a group field. */
export function groupFieldLabel(key: string): string | null {
  const name = groupFieldName(key)
  return name ? name.charAt(0).toUpperCase() + name.slice(1) : null
}

/**
 * The changes with each known field's name put into plain words, in the order they came, and a
 * count written with its thousands separated the way the count sentences write it.
 */
export function withPlainNames(changed: [string, Pair][]): [string, Pair][] {
  return changed.map(([key, pair]) => {
    const name = groupFieldName(key)
    if (!name) return [key, pair]

    return [name, isCount(key) ? { old: written(pair.old), new: written(pair.new) } : pair]
  })
}

function isCount(key: string): boolean {
  const field = key.toLowerCase()
  return field === 'membercount' || field === 'onlinemembercount'
}

function written(value: unknown): unknown {
  return typeof value === 'number' ? value.toLocaleString() : value
}

const COUNT_FIELDS = ['membercount', 'onlinemembercount'] as const

const SAY: Record<(typeof COUNT_FIELDS)[number], string> = {
  membercount: 'Members',
  onlinemembercount: 'Members online',
}

/**
 * The sentences for a group entry that is only a count moving, or null when it is anything else.
 *
 * "Members online went from 260 to 263." One sentence for each count that moved, the member count
 * first. Null when any other field changed, when nothing did, or when a count has no number on one
 * side -- those are said as the edit they are.
 */
export function countReadings(changed: [string, Pair][]): string[] | null {
  if (changed.length === 0) return null

  const found: Partial<Record<(typeof COUNT_FIELDS)[number], [number, number]>> = {}

  for (const [key, pair] of changed) {
    const field = key.toLowerCase()
    if (field !== 'membercount' && field !== 'onlinemembercount') return null
    if (typeof pair.old !== 'number' || typeof pair.new !== 'number') return null
    found[field] = [pair.old, pair.new]
  }

  const sentences: string[] = []

  for (const field of COUNT_FIELDS) {
    const pair = found[field]
    if (pair) sentences.push(`${SAY[field]} went from ${pair[0].toLocaleString()} to ${pair[1].toLocaleString()}.`)
  }

  return sentences
}
