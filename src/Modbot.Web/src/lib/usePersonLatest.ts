import { useCallback } from 'react'
import { api } from './api.ts'
import type { PersonAsked } from './personTimeline.ts'
import { useLoad } from './useLoad.ts'

/**
 * The newest few facts about any of the person's accounts or done by them, for the Overview tab.
 *
 * One read: the server ties the accounts together and merges them, the same as the Activity tab.
 */
export function usePersonLatest(asked: PersonAsked, limit: number) {
  const load = useCallback(
    () => api.audit({ person: asked.id, personPlatform: asked.platform, limit }),
    [asked.id, asked.platform, limit],
  )
  return useLoad(load)
}
