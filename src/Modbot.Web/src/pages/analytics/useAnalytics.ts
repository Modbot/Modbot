import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '@/lib/api'

/**
 * The date range every Analytics page offers, and the hook that loads a page for it.
 *
 * In its own file, with no components, so the pages' fast-refresh boundary holds and so every
 * page means exactly the same thing by "30d": the same query string, built in one place.
 */

export type Range = 7 | 30 | 90 | 'all'

export const RANGES: { range: Range; label: string }[] = [
  { range: 7, label: '7d' },
  { range: 30, label: '30d' },
  { range: 90, label: '90d' },
  { range: 'all', label: 'All time' },
]

export const rangeQuery = (range: Range): string => (range === 'all' ? 'all=true' : `days=${range}`)

/**
 * Loads one page's data for a range, once per range change. The previous range's data stays on
 * screen until the new one arrives, so switching ranges does not blank the page.
 *
 * A 403 is worded as a permission problem rather than a failure, because from the reader's side
 * that is what it is, and a generic error would send them to check the server.
 *
 * `enabled` false holds off the read, for a part of the Stats page whose tab is not open yet. What
 * was read stays, so going back to a tab does not read it again for the same range.
 */
export function useAnalytics<T>(load: (query: string) => Promise<T>, range: Range, enabled = true) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const readFor = useRef<string | null>(null)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    const query = rangeQuery(range)
    if (!enabled || readFor.current === query) return
    let cancelled = false

    load(query)
      .then((next) => {
        if (!cancelled) {
          readFor.current = query
          setData(next)
          setError(null)
        }
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read analytics.'
            : 'Could not load this page.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [load, range, enabled, tries])

  // The failed panel's "Try again": the same range read again, keeping the range the page is on.
  const reload = useCallback(() => {
    readFor.current = null
    setError(null)
    setTries((n) => n + 1)
  }, [])

  return { data, error, reload }
}
