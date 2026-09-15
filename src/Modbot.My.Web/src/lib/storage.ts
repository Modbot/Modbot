import { normaliseInstanceUrl } from './instanceUrl.ts'
import type { SavedInstance } from './merge.ts'

/** Saved instances. The key and shape the old hand-written page used, so an old list still loads. */
export const SAVED_KEY = 'modbot.instances'

/** What was opened from here, and when. Newest first. */
export const HISTORY_KEY = 'modbot.history'

/** URLs removed in this browser that the server may still list for this IP address. */
export const HIDDEN_KEY = 'modbot.hidden'

const HISTORY_LIMIT = 200

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

function text(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

export function loadSaved(): SavedInstance[] {
  const raw = read(SAVED_KEY)
  if (!Array.isArray(raw)) return []

  const list: SavedInstance[] = []
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue
    const entry = item as Record<string, unknown>

    const url = normaliseInstanceUrl(text(entry.url))
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

/** Saves the instance, or marks a saved one as just used. */
export function saveInstance(url: string): void {
  const now = new Date().toISOString()
  const list = loadSaved()

  const existing = list.find((i) => i.url === url)
  if (existing) existing.lastUsedAt = now
  else list.push({ url, name: null, addedAt: now, lastUsedAt: now })

  write(SAVED_KEY, list)
  write(HIDDEN_KEY, loadHidden().filter((u) => u !== url))
}

/** Removes the instance from this browser, including from the server's list as shown here. */
export function removeInstance(url: string): void {
  write(SAVED_KEY, loadSaved().filter((i) => i.url !== url))

  const hidden = loadHidden()
  if (!hidden.includes(url)) write(HIDDEN_KEY, [...hidden, url])
}

/** Saves the instance and adds what was opened to the history. */
export function recordUse(url: string, path: string, action: HistoryAction): void {
  saveInstance(url)

  const entry: HistoryEntry = { url, path, action, at: new Date().toISOString() }
  write(HISTORY_KEY, [entry, ...loadHistory()].slice(0, HISTORY_LIMIT))
}
