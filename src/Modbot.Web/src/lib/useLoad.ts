import { useCallback, useEffect, useState } from 'react'
import { ApiError } from '@/lib/api'

/**
 * Loads one thing when the popup opens, and says plainly when it cannot.
 *
 * Every popup is opened speculatively — somebody noticed a name mid-scan — so a failure has to
 * read as a sentence rather than as an empty panel.
 *
 * `version` reloads when it changes -- a live event about what is on screen (`useLiveVersion`)
 * -- and keeps what is shown until the new answer lands, so a redraw never blanks the panel. A
 * string lets a caller combine two reasons to reload without one hiding a change in the other.
 *
 * `reload` is the failed row's "Try again" (`EmptyRow`'s `onTryAgain`): it clears the failure, so
 * the row turns back into the loading bars while the read runs again, and keeps anything already
 * shown, the same as a `version` change. An answer that lands clears an earlier failure too, so a
 * read that failed once and then worked never leaves the failure on screen beside the answer.
 */
export function useLoad<T>(
  load: (() => Promise<T>) | null,
  version: number | string = 0,
): { data: T | null; error: string | null; reload: () => void } {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    if (!load) return
    let cancelled = false

    load()
      .then((next) => {
        if (cancelled) return
        setData(next)
        setError(null)
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
  }, [load, version, tries])

  const reload = useCallback(() => {
    setError(null)
    setTries((n) => n + 1)
  }, [])

  return { data, error, reload }
}
