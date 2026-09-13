import { useEffect, useState } from 'react'
import { ApiError } from '@/lib/api'

/**
 * The date range every Analytics page offers, and the hook that loads a page for it.
 *
 * In its own file, with no components, so the pages' fast-refresh boundary holds and so every
 * page means exactly the same thing by "30 days": the same query string, built in one place.
 */

export type Range = 7 | 30 | 90 | 'all'

export const RANGES: { range: Range; label: string }[] = [
  { range: 7, label: '7 days' },
  { range: 30, label: '30 days' },
  { range: 90, label: '90 days' },
  { range: 'all', label: 'All time' },
]

export const rangeQuery = (range: Range): string => (range === 'all' ? 'all=true' : `days=${range}`)

/**
 * Loads one page's data for a range, once per range change. The previous range's data stays on
 * screen until the new one arrives, so switching ranges does not blank the page.
 *
 * A 403 is worded as a permission problem rather than a failure, because from the reader's side
 * that is what it is, and a generic error would send them to check the server.
 */
export function useAnalytics<T>(load: (query: string) => Promise<T>, range: Range) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    load(rangeQuery(range))
      .then((next) => {
        if (!cancelled) {
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
  }, [load, range])

  return { data, error }
}
