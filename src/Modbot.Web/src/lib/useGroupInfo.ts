import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, type GroupInfo } from '@/lib/api'

/**
 * The last group read, shared by every tab of the VRChat page. Each tab is its own page, so
 * without it every tab change started from nothing and the page flashed "Loading…" in place of
 * the header it had a moment ago. A tab now draws what the last one read and reads again quietly.
 */
let lastRead: GroupInfo | null = null

/**
 * The group as Modbot last read it, for the top of every tab of the VRChat page. Read from what the
 * group-info sync stored, so it asks VRChat nothing; `setInfo` takes the group a Save answered with,
 * so an edit shows at once.
 */
export function useGroupInfo(): {
  info: GroupInfo | null
  error: string | null
  setInfo: (info: GroupInfo) => void
} {
  const [info, setShown] = useState<GroupInfo | null>(lastRead)
  const [error, setError] = useState<string | null>(null)

  const setInfo = useCallback((next: GroupInfo) => {
    lastRead = next
    setShown(next)
  }, [])

  useEffect(() => {
    let cancelled = false

    api
      .groupInfo()
      .then((i) => {
        if (!cancelled) setInfo(i)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not load the group.')
      })

    return () => {
      cancelled = true
    }
  }, [setInfo])

  return { info, error, setInfo }
}
