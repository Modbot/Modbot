import { useEffect, useState } from 'react'
import { ApiError } from '@/lib/api'

/**
 * Loads one thing when the popup opens, and says plainly when it cannot.
 *
 * Every popup is opened speculatively — somebody noticed a name mid-scan — so a failure has to
 * read as a sentence rather than as an empty panel.
 */
export function useLoad<T>(load: (() => Promise<T>) | null): { data: T | null; error: string | null } {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!load) return
    let cancelled = false

    load()
      .then((next) => {
        if (!cancelled) setData(next)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError && e.status === 404
              ? 'Modbot has no record of this.'
              : 'Could not load this.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [load])

  return { data, error }
}
