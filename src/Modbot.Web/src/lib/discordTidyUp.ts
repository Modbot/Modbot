// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
import type { QuietChannelQuery, QuietChannelRow, RoleFlag } from './api.ts'
import { chipFor, yesNo, type FilterChip } from './filters.ts'
import { howLong } from './format.ts'

/**
 * The words the Discord page's Roles and Channels tabs put on screen (Discord tidy-up design). The
 * server decides what is marked and in which order; this only names it.
 */

/** Each mark the roles report puts on a role, as the page writes it. */
export const ROLE_FLAG_LABELS: Record<RoleFlag, string> = {
  'no-members': 'No members',
  'same-name': 'Same name as another role',
  'same-permissions-and-colour': 'Same permissions and colour as another role',
  'bot-role': 'Bot role',
}

/**
 * How long a channel has been quiet, against the server's clock, in Modbot's units with no weeks:
 * "5h", "3mth", "2y". When no last message is known it says why instead: the bot cannot read the
 * channel, its history is still being read back, or nobody has ever written in it.
 */
export function quietFor(channel: QuietChannelRow, now: string): string {
  if (!channel.canRead) return "Can't read"
  if (channel.lastMessageAt) return howLong(channel.lastMessageAt, now)
  return channel.stillReading ? 'Still reading' : 'No messages'
}

// ── Sorting ─────────────────────────────────────────────────────────────────────────────────────

/** The Channels tab's columns, each one a way to sort. */
export type ChannelSortKey = 'name' | 'category' | 'last' | 'quiet'

export type ChannelSort = { key: ChannelSortKey; dir: 'asc' | 'desc' }

/** The words for each column, as the headers and the sort box write them. */
export const CHANNEL_SORT_LABELS: Record<ChannelSortKey, string> = {
  name: 'Channel',
  category: 'Category',
  last: 'Last message',
  quiet: 'Quiet for',
}

/** Quietest first, as the tab has always opened. */
export const CHANNEL_SORT_DEFAULT: ChannelSort = { key: 'quiet', dir: 'desc' }

/** The sort an address's `sort` asks for ("last-asc"), or the default when it asks for none or something else. */
export function readChannelSort(value: string | null): ChannelSort {
  const [key, dir] = (value ?? '').split('-')
  if (!Object.keys(CHANNEL_SORT_LABELS).includes(key) || (dir !== 'asc' && dir !== 'desc')) return CHANNEL_SORT_DEFAULT
  return { key: key as ChannelSortKey, dir }
}

/** What to put in the address for a sort: nothing for the default, so a bare address stays bare. */
export function channelSortParam(sort: ChannelSort): string | null {
  return sort.key === CHANNEL_SORT_DEFAULT.key && sort.dir === CHANNEL_SORT_DEFAULT.dir ? null : `${sort.key}-${sort.dir}`
}

/** The direction a column opens in when it is first clicked: text from A, times from the longest quiet. */
export function firstDirection(key: ChannelSortKey): 'asc' | 'desc' {
  return key === 'name' || key === 'category' ? 'asc' : 'desc'
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })

/**
 * How long a channel has been quiet in milliseconds: infinite when nobody has written there, and
 * null when that is not known (the bot cannot read it, or is still reading its history).
 */
function quietMs(channel: QuietChannelRow, now: string): number | null {
  if (!channel.canRead) return null
  if (channel.lastMessageAt) return Date.parse(now) - Date.parse(channel.lastMessageAt)
  return channel.stillReading ? null : Infinity
}

/** Where a channel with no time to sort by falls: still reading before cannot read, both after every other row. */
function unknownRank(channel: QuietChannelRow): number {
  return channel.canRead ? 0 : 1
}

/**
 * The channels in the order asked for. Rows with nothing to sort by in a column -- no category, no
 * last message, a quiet time not known -- come last in both directions, so reversing the order
 * does not bring them to the top. Rows that tie keep the order they came in.
 */
export function sortChannels(channels: readonly QuietChannelRow[], sort: ChannelSort, now: string): QuietChannelRow[] {
  const sign = sort.dir === 'asc' ? 1 : -1

  // A number for the column, or null for a row with none.
  const number = (c: QuietChannelRow): number | null => {
    switch (sort.key) {
      case 'last':
        return c.canRead && c.lastMessageAt ? Date.parse(c.lastMessageAt) : null
      case 'quiet':
        return quietMs(c, now)
      default:
        return null
    }
  }

  return [...channels].sort((a, b) => {
    if (sort.key === 'name') return sign * collator.compare(a.name, b.name)

    if (sort.key === 'category') {
      if (a.categoryName === null || b.categoryName === null) {
        return a.categoryName === b.categoryName ? 0 : a.categoryName === null ? 1 : -1
      }
      return sign * collator.compare(a.categoryName, b.categoryName)
    }

    const x = number(a)
    const y = number(b)
    if (x === null || y === null) {
      if (x === y) return unknownRank(a) - unknownRank(b)
      return x === null ? 1 : -1
    }
    if (x === y) return 0
    return sign * (x < y ? -1 : 1)
  })
}

// ── Filtering ───────────────────────────────────────────────────────────────────────────────────

/** Each way a channel's row can read in "Quiet for" when no time is shown. */
export type ChannelStatus = 'has-messages' | 'no-messages' | 'still-reading' | 'cant-read'

export const CHANNEL_STATUS_LABELS: Record<ChannelStatus, string> = {
  'has-messages': 'Has messages',
  'no-messages': 'No messages',
  'still-reading': 'Still reading',
  'cant-read': "Can't read",
}

export function channelStatus(channel: QuietChannelRow): ChannelStatus {
  if (!channel.canRead) return 'cant-read'
  if (channel.lastMessageAt) return 'has-messages'
  return channel.stillReading ? 'still-reading' : 'no-messages'
}

/** "Quiet for" at least this many days, as the filter offers them. */
export const QUIET_DAYS = [7, 30, 60, 90, 180, 365]

/** The kinds of channel the tab lists, as Discord's list names them. */
export const CHANNEL_TYPE_LABELS: Record<QuietChannelRow['type'], string> = {
  text: 'Text',
  announcement: 'Announcement',
  forum: 'Forum',
}

/** The value a channel with no category has in the category filter. */
export const NO_CATEGORY = 'none'

/** What the chips ask of the server: the channels some role can see, and the channels no role can see. */
export function channelQueryFrom(chips: FilterChip[]): QuietChannelQuery {
  const role = chipFor(chips, 'seenBy')
  if (!role || role.values.length === 0) return {}
  return role.operator === 'is-not' ? { notSeenBy: role.values } : { seenBy: role.values }
}

/**
 * The channels the rest of the chips and the search words keep. "Seen by" is the server's to work
 * out, since it takes the overwrites, so it is not looked at here.
 */
export function filterChannels(
  channels: readonly QuietChannelRow[],
  chips: FilterChip[],
  search: string,
  now: string,
): QuietChannelRow[] {
  const words = search.trim().toLowerCase()
  const days = Number(chipFor(chips, 'quiet')?.values[0])
  const staffOnly = yesNo(chips, 'staffOnly')

  // "is" keeps rows whose value is among the chip's, "is not" the rest.
  const among = (property: string, value: string): boolean => {
    const chip = chipFor(chips, property)
    if (!chip || chip.values.length === 0) return true
    return chip.values.includes(value) === (chip.operator !== 'is-not')
  }

  return channels.filter((c) => {
    if (words && !c.name.toLowerCase().includes(words)) return false
    if (staffOnly !== undefined && c.staffOnly !== staffOnly) return false
    if (!among('type', c.type)) return false
    if (!among('category', c.categoryId ?? NO_CATEGORY)) return false
    if (!among('status', channelStatus(c))) return false

    if (days > 0) {
      const quiet = quietMs(c, now)
      if (quiet === null || quiet < days * 86_400_000) return false
    }

    return true
  })
}
