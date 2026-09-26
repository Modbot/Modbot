import type { SavedServer, SeenServer } from './merge.ts'
import { normaliseServerUrl } from './serverUrl.ts'

/** Servers saved in this browser, in the shape the old hand-written page used. */
export const SAVED_KEY = 'modbot.servers'

/**
 * Where the saved list lived until 2026-09-26, when "instance" became "server".
 *
 * Compatibility: the list is moved from here to `SAVED_KEY` the first time it is read. Remove once
 * every browser that saved a server before that date has been back since.
 */
export const OLD_SAVED_KEY = 'modbot.instances'

/** What was opened from here, and when. Newest first. */
export const HISTORY_KEY = 'modbot.history'

/** URLs removed in this browser that my.modbot.co may still list for this IP address. */
export const HIDDEN_KEY = 'modbot.hidden'

/** Server addresses this browser still has to send to my.modbot.co. Oldest first. */
export const OUTBOX_KEY = 'modbot.outbox'

/**
 * my.modbot.co's last list of servers seen from this IP address, shown while a fresh one cannot be
 * had.
 */
export const SEEN_KEY = 'modbot.seen-servers'

/**
 * Where that list lived until 2026-09-26.
 *
 * Compatibility: moved to `SEEN_KEY` the first time it is read. Remove along with `OLD_SAVED_KEY`.
 */
export const OLD_SEEN_KEY = 'modbot.server-instances'

const HISTORY_LIMIT = 200

const OUTBOX_LIMIT = 50

export type OutboxEntry = {
  url: string
  addedAt: string
  /** Sends that failed. Zero until the first one has, so a send that just works shows nothing. */
  tries: number
}

export type HistoryAction = 'register' | 'go' | 'open'

export type HistoryEntry = {
  url: string
  path: string
  action: HistoryAction
  at: string
}

// A blocked or corrupt store reads as empty and ignores writes. Private windows and locked-down
// browsers both land here, and the page must still work in them.
function read(key: string): unknown {
  try {
    return JSON.parse(localStorage.getItem(key) ?? 'null')
  } catch {
    return null
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // See read().
  }
}

/**
 * Moves a value from its old key to its new one, when only the old one has it. Nothing happens once
 * the new key holds something, so a list saved since is never overwritten.
 *
 * Compatibility, added 2026-09-26 for the keys renamed from "instance" to "server". Remove with
 * `OLD_SAVED_KEY` and `OLD_SEEN_KEY`.
 */
function moveOldKey(from: string, to: string): void {
  try {
    if (localStorage.getItem(to) !== null) return
    const old = localStorage.getItem(from)
    if (old === null) return
    localStorage.setItem(to, old)
    localStorage.removeItem(from)
  } catch {
    // See read().
  }
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

export function loadSaved(): SavedServer[] {
  moveOldKey(OLD_SAVED_KEY, SAVED_KEY)

  const raw = read(SAVED_KEY)
  if (!Array.isArray(raw)) return []

  const list: SavedServer[] = []
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue
    const entry = item as Record<string, unknown>

    const url = normaliseServerUrl(text(entry.url))
    if (!url || list.some((i) => i.url === url)) continue

    list.push({
      url,
      name: text(entry.name),
      addedAt: text(entry.addedAt) ?? new Date(0).toISOString(),
      lastUsedAt: text(entry.lastUsedAt) ?? undefined,
    })
  }

  return list
}

export function loadHidden(): string[] {
  const raw = read(HIDDEN_KEY)
  return Array.isArray(raw) ? raw.filter((u): u is string => typeof u === 'string') : []
}

export function loadHistory(): HistoryEntry[] {
  const raw = read(HISTORY_KEY)
  return Array.isArray(raw) ? (raw.filter((e) => e && typeof e === 'object') as HistoryEntry[]) : []
}

export function loadOutbox(): OutboxEntry[] {
  const raw = read(OUTBOX_KEY)
  if (!Array.isArray(raw)) return []

  const list: OutboxEntry[] = []
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue
    const entry = item as Record<string, unknown>

    const url = normaliseServerUrl(text(entry.url))
    if (!url || list.some((e) => e.url === url)) continue

    const tries = typeof entry.tries === 'number' && entry.tries >= 0 ? Math.floor(entry.tries) : 0
    list.push({ url, addedAt: text(entry.addedAt) ?? new Date(0).toISOString(), tries })
  }

  return list
}

export function saveOutbox(entries: readonly OutboxEntry[]): void {
  // The newest entries are the ones somebody is waiting on; the oldest are the ones to let go of.
  write(OUTBOX_KEY, entries.slice(-OUTBOX_LIMIT))
}

/**
 * The seen servers in a list my.modbot.co sent or this browser kept. An entry missing what matters is
 * skipped rather than trusted.
 */
export function seenServersFrom(items: unknown): SeenServer[] {
  if (!Array.isArray(items)) return []

  const list: SeenServer[] = []
  for (const item of items) {
    if (!item || typeof item !== 'object') continue
    const entry = item as Record<string, unknown>

    // Compatibility, added 2026-09-26: `instanceUrl` is the field's name before then, in a list
    // this browser kept or from a my.modbot.co not yet updated. Remove once neither can be.
    const serverUrl = text(entry.serverUrl) ?? text(entry.instanceUrl)
    const lastSeenAt = text(entry.lastSeenAt)
    if (!serverUrl || !lastSeenAt) continue

    list.push({
      serverUrl,
      firstSeenAt: text(entry.firstSeenAt) ?? lastSeenAt,
      lastSeenAt,
      visits: typeof entry.visits === 'number' ? entry.visits : 0,
    })
  }

  return list
}

export function loadSeenServers(): SeenServer[] {
  moveOldKey(OLD_SEEN_KEY, SEEN_KEY)

  const raw = read(SEEN_KEY)
  return seenServersFrom(raw && typeof raw === 'object' ? (raw as { items?: unknown }).items : null)
}

export function saveSeenServers(items: readonly SeenServer[]): void {
  write(SEEN_KEY, { items, savedAt: new Date().toISOString() })
}

/** Saves the server, or marks a saved one as just used. */
export function saveServer(url: string): void {
  const now = new Date().toISOString()
  const list = loadSaved()

  const existing = list.find((i) => i.url === url)
  if (existing) existing.lastUsedAt = now
  else list.push({ url, name: null, addedAt: now, lastUsedAt: now })

  write(SAVED_KEY, list)
  write(HIDDEN_KEY, loadHidden().filter((u) => u !== url))
}

/** Removes the server from this browser, including from the seen list as shown here. */
export function removeServer(url: string): void {
  write(SAVED_KEY, loadSaved().filter((i) => i.url !== url))

  const hidden = loadHidden()
  if (!hidden.includes(url)) write(HIDDEN_KEY, [...hidden, url])
}

/** Saves the server and adds what was opened to the history. */
export function recordUse(url: string, path: string, action: HistoryAction): void {
  saveServer(url)

  const entry: HistoryEntry = { url, path, action, at: new Date().toISOString() }
  write(HISTORY_KEY, [entry, ...loadHistory()].slice(0, HISTORY_LIMIT))
}
