import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { ApiError } from './api.ts'

/** Called when an admin request comes back 401: the session ended, so show the sign-in form. */
export const SignedOutContext = createContext<() => void>(() => {})

/**
 * Loads admin data, and re-loads when `deps` change or `reload` is called.
 */
export function useAdminLoad<T>(
  load: () => Promise<T>,
  deps: readonly unknown[],
): { data: T | null; error: ApiError | null; reload: () => void } {
  const signedOut = useContext(SignedOutContext)
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let live = true
    load()
      .then((result) => {
        if (!live) return
        setData(result)
        setError(null)
      })
      .catch((e: unknown) => {
        if (!live) return
        const failure = e instanceof ApiError ? e : new ApiError(0, 'Could not reach the server.')
        if (failure.status === 401) signedOut()
        setError(failure)
      })
    return () => {
      live = false
    }
    // load is a fresh closure every render; deps say when it means something new.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, version])

  const reload = useCallback(() => setVersion((v) => v + 1), [])

  return { data, error, reload }
}
