import { normaliseServerUrl } from './serverUrl.ts'

/** A server saved in this browser, in the shape the old hand-written page wrote. */
export type SavedServer = {
  url: string
  name: string | null
  addedAt: string
  lastUsedAt?: string
}

/** A server Modbot Cloud has seen from this browser's IP address, as my.modbot.co reported it. */
export type SeenServer = {
  serverUrl: string
  firstSeenAt: string
  lastSeenAt: string
  visits: number
}

/** One entry of the combined list. */
export type KnownServer = {
  url: string
  name: string | null
  lastUsedAt: string
}

function time(iso: string): number {
  const parsed = Date.parse(iso)
  return Number.isNaN(parsed) ? 0 : parsed
}

/**
 * The single list `/`, `/register` and `/go` show: the servers saved in this browser and the ones
 * seen from this IP address, one entry per URL, most recently used first.
 *
 * `hidden` holds URLs removed in this browser. A seen entry for one of those is left out, so
 * removing a server sticks even though Cloud still has it; saving it again un-hides it.
 */
export function mergeServers(
  saved: readonly SavedServer[],
  seen: readonly SeenServer[],
  hidden: readonly string[],
): KnownServer[] {
  const byUrl = new Map<string, KnownServer>()

  for (const server of saved) {
    byUrl.set(server.url, {
      url: server.url,
      name: server.name,
      lastUsedAt: server.lastUsedAt ?? server.addedAt,
    })
  }

  for (const server of seen) {
    const url = normaliseServerUrl(server.serverUrl)
    if (!url) continue

    const existing = byUrl.get(url)
    if (existing) {
      if (time(server.lastSeenAt) > time(existing.lastUsedAt)) existing.lastUsedAt = server.lastSeenAt
    } else if (!hidden.includes(url)) {
      byUrl.set(url, { url, name: null, lastUsedAt: server.lastSeenAt })
    }
  }

  return [...byUrl.values()].sort(
    (a, b) => time(b.lastUsedAt) - time(a.lastUsedAt) || a.url.localeCompare(b.url),
  )
}
