import { useCallback, useState } from 'react'
import { ApiError, type MissingGroupPermission } from '@/lib/api'
import { missingPermissionOf } from '@/lib/vrchatPermissions'

/**
 * One Save at a time, and what went wrong with the last. `run` resolves true once the request was
 * accepted, false when it was refused, and never throws. A refusal's words are the server's, which
 * for a write VRChat turned down are VRChat's own; `missing` is set when VRChat refused because
 * Modbot's own VRChat account lacks a group permission, for `SaveCancel` to name it.
 */
export function useSave(failed = 'Could not save.') {
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [missing, setMissing] = useState<MissingGroupPermission | null>(null)

  const run = useCallback(
    async (send: () => Promise<unknown>): Promise<boolean> => {
      setSaving(true)
      setProblem(null)
      setMissing(null)
      try {
        await send()
        return true
      } catch (e) {
        setProblem(e instanceof ApiError ? e.message : failed)
        setMissing(e instanceof ApiError ? missingPermissionOf(e.detail) : null)
        return false
      } finally {
        setSaving(false)
      }
    },
    [failed],
  )

  const clear = useCallback(() => {
    setProblem(null)
    setMissing(null)
  }, [])

  return { saving, problem, missing, run, clear }
}
