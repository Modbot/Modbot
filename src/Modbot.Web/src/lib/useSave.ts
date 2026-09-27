import { useCallback, useState } from 'react'
import { ApiError } from '@/lib/api'

/**
 * One Save at a time, and what went wrong with the last. `run` resolves true once the request was
 * accepted, false when it was refused, and never throws. A refusal's words are the server's, which
 * for a write VRChat turned down are VRChat's own.
 */
export function useSave(failed = 'Could not save.') {
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const run = useCallback(
    async (send: () => Promise<unknown>): Promise<boolean> => {
      setSaving(true)
      setProblem(null)
      try {
        await send()
        return true
      } catch (e) {
        setProblem(e instanceof ApiError ? e.message : failed)
        return false
      } finally {
        setSaving(false)
      }
    },
    [failed],
  )

  const clear = useCallback(() => setProblem(null), [])

  return { saving, problem, run, clear }
}
