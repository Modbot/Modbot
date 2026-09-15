import { normaliseInstanceUrl } from './instanceUrl.ts'

/** An instance saved in this browser, in the `modbot.instances` shape the old page wrote. */
export type SavedInstance = {
  url: string
  name: string | null
  addedAt: string
  lastUsedAt?: string
}

/** An instance the server has seen from this browser's IP address. */
export type ServerInstance = {
  instanceUrl: string
  firstSeenAt: string
  lastSeenAt: string
  visits: number
}

/** One entry of the combined list. */
export type KnownInstance = {
  url: string
  name: string | null
  lastUsedAt: string
}

function time(iso: string): number {
  const parsed = Date.parse(iso)
  return Number.isNaN(parsed) ? 0 : parsed
}

/**
 * The single list `/`, `/register` and `/go` show: this browser's saved instances and the server's
 * instances for this IP address, one entry per URL, most recently used first.
 *
 * `hidden` holds URLs removed in this browser. A server entry for one of those is left out, so
 * removing an instance sticks even though the server still has it; saving it again un-hides it.
 */
export function mergeInstances(
  saved: readonly SavedInstance[],
  server: readonly ServerInstance[],
  hidden: readonly string[],
): KnownInstance[] {
  const byUrl = new Map<string, KnownInstance>()

  for (const instance of saved) {
    byUrl.set(instance.url, {
      url: instance.url,
      name: instance.name,
      lastUsedAt: instance.lastUsedAt ?? instance.addedAt,
    })
  }

  for (const instance of server) {
    const url = normaliseInstanceUrl(instance.instanceUrl)
    if (!url) continue

    const existing = byUrl.get(url)
    if (existing) {
      if (time(instance.lastSeenAt) > time(existing.lastUsedAt)) existing.lastUsedAt = instance.lastSeenAt
    } else if (!hidden.includes(url)) {
      byUrl.set(url, { url, name: null, lastUsedAt: instance.lastSeenAt })
    }
  }

  return [...byUrl.values()].sort(
    (a, b) => time(b.lastUsedAt) - time(a.lastUsedAt) || a.url.localeCompare(b.url),
  )
}
