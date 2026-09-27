import { useEffect, useState } from 'react'
import { api, ApiError, type GroupInfo } from '@/lib/api'

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
  const [info, setInfo] = useState<GroupInfo | null>(null)
  const [error, setError] = useState<string | null>(null)

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
  }, [])

  return { info, error, setInfo }
}
