import { useCallback } from 'react'
import { api, ApiError, type CurrentUser } from '@/lib/api'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { can } from '@/lib/permissions'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { useLoad } from '@/lib/useLoad'

/**
 * The Discord halves of the one record a person's JSON tab shows, as values rather than as
 * panels.
 *
 * A hook rather than a component because that tab is one document: a person's Discord rows sit
 * beside their VRChat ones under named keys, and a component that drew its own panel could not
 * be folded into that.
 *
 * Takes a null id, and asks for nothing when given one, so the caller can hold the hook's place
 * for a person who has no Discord account.
 */
export function useDiscordRecords(id: string | null, me: CurrentUser) {
  const live = useLiveVersion(
    useCallback((event: LiveEvent) => (id === null ? false : concernsPerson(event, id, 'Discord')), [id]),
  )

  const loadMember = useCallback(
    () =>
      api
        .discordMember(id!)
        .catch((e: unknown) => (e instanceof ApiError && e.status === 404 ? null : Promise.reject(e))),
    [id],
  )
  const member = useLoad(id !== null && can(me, 'ViewMembers') ? loadMember : null, live)

  const loadMetrics = useCallback(() => api.discordMemberMetrics(id!), [id])
  const metrics = useLoad(id !== null && can(me, 'ViewProfile') ? loadMetrics : null, live)

  return {
    member: id !== null && can(me, 'ViewMembers') ? (member.error ?? member.data) : undefined,
    activity: id !== null && can(me, 'ViewProfile') ? (metrics.error ?? metrics.data) : undefined,
  }
}
