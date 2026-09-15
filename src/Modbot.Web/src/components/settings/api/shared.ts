import { ApiError } from '@/lib/api'

/** The sentence to show for a failed call on the API tabs. */
export function failure(e: unknown, fallback: string): string {
  if (e instanceof ApiError && e.status === 403) return 'You do not have permission to do that.'
  if (e instanceof ApiError && e.status !== 0) return e.message
  return e instanceof ApiError ? e.message : fallback
}

/** A date and time, in the reader's own format. */
export function when(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
