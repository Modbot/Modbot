import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api.ts'
import { mergeInstances, type KnownInstance, type ServerInstance } from './merge.ts'
import { keepTrying } from './retry.ts'
import { loadHidden, loadSaved, loadServerList, removeInstance, saveInstance, saveServerList } from './storage.ts'

/**
 * The combined list: saved in this browser plus seen from this IP address.
 *
 * The server's half starts as the last answer this browser kept, so the page is whole even before
 * the server has been asked, and stays whole when it cannot be. A fresh answer replaces it; a
 * failure leaves it and asks again later — after 5 s, 10 s, 20 s and so on up to five minutes, and
 * at once when the browser comes back online.
 *
 * `loaded` turns true once the server has answered, or failed to. `/go` waits for it, so the list
 * does not change under someone's cursor when the server's half arrives.
 */
export function useKnownInstances(): {
  instances: KnownInstance[]
  loaded: boolean
  add: (url: string) => void
  remove: (url: string) => void
} {
  const [server, setServer] = useState<ServerInstance[]>(loadServerList)
  const [loaded, setLoaded] = useState(false)
  // Bumped after a change to localStorage, so the list reads it again.
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let live = true

    const refresh = keepTrying(async () => {
      try {
        const answer = await api.myInstances()
        if (!live) return true
        saveServerList(answer.items)
        setServer(answer.items)
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

  const instances = useMemo(
    () => mergeInstances(loadSaved(), server, loadHidden()),
    // version is the signal that localStorage changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [server, version],
  )

  const add = useCallback((url: string) => {
    saveInstance(url)
    setVersion((v) => v + 1)
  }, [])

  const remove = useCallback((url: string) => {
    removeInstance(url)
    setVersion((v) => v + 1)
  }, [])

  return { instances, loaded, add, remove }
}
