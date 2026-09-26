import { ApiError } from '@/lib/api'
import { dateTime } from '@/components/charts/format'

/** The sentence to show for a failed call on the API tabs. */
export function failure(e: unknown, fallback: string): string {
  if (e instanceof ApiError && e.status === 403) return 'You do not have permission to do that.'
  if (e instanceof ApiError && e.status !== 0) return e.message
  return e instanceof ApiError ? e.message : fallback
}

/** A date and time, in the reader's own format. */
export const when = dateTime
