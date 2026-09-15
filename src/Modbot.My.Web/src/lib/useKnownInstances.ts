import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api.ts'
import { mergeInstances, type KnownInstance, type ServerInstance } from './merge.ts'
import { loadHidden, loadSaved, removeInstance, saveInstance } from './storage.ts'

/**
 * The combined list: saved in this browser plus seen from this IP address.
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
  const [server, setServer] = useState<ServerInstance[]>([])
  const [loaded, setLoaded] = useState(false)
  // Bumped after a change to localStorage, so the list reads it again.
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let live = true
    api
      .myInstances()
      .then((r) => live && setServer(r.items))
      .catch(() => live && setServer([]))
      .finally(() => live && setLoaded(true))
    return () => {
      live = false
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
