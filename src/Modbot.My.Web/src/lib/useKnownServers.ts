import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api.ts'
import { mergeServers, type KnownServer, type SeenServer } from './merge.ts'
import { keepTrying } from './retry.ts'
import { loadHidden, loadSaved, loadSeenServers, removeServer, saveSeenServers, saveServer } from './storage.ts'

/**
 * The combined list: saved in this browser plus seen from this IP address.
 *
 * The seen half starts as the last list this browser kept, so the page is whole even before
 * my.modbot.co has been asked, and stays whole when it cannot be. A fresh list replaces it; a
 * failure leaves it and asks again later — after 5 s, 10 s, 20 s and so on up to five minutes, and
 * at once when the browser comes back online.
 *
 * `loaded` turns true once my.modbot.co has answered, or failed to. `/go` waits for it, so the list
 * does not change under someone's cursor when the seen half arrives.
 */
export function useKnownServers(): {
  servers: KnownServer[]
  loaded: boolean
  add: (url: string) => void
  remove: (url: string) => void
} {
  const [seen, setSeen] = useState<SeenServer[]>(loadSeenServers)
  const [loaded, setLoaded] = useState(false)
  // Bumped after a change to localStorage, so the list reads it again.
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let live = true

    const refresh = keepTrying(async () => {
      try {
        const answer = await api.myServers()
        if (!live) return true
        saveSeenServers(answer.items)
        setSeen(answer.items)
        return true
      } catch {
        return false
      } finally {
        if (live) setLoaded(true)
      }
    })

    const onOnline = () => refresh.now()
    window.addEventListener('online', onOnline)
    refresh.now()

    return () => {
      live = false
      refresh.stop()
      window.removeEventListener('online', onOnline)
    }
  }, [])

  const servers = useMemo(
    () => mergeServers(loadSaved(), seen, loadHidden()),
    // version is the signal that localStorage changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [seen, version],
  )

  const add = useCallback((url: string) => {
    saveServer(url)
    setVersion((v) => v + 1)
  }, [])

  const remove = useCallback((url: string) => {
    removeServer(url)
    setVersion((v) => v + 1)
  }, [])

  return { servers, loaded, add, remove }
}
