import { useCallback } from 'react'
import { api, ApiError, type DiscordMember } from '@/lib/api'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { useLoad } from '@/lib/useLoad'

export type DiscordMemberRead = {
  data: { member: DiscordMember | null; timedOut: boolean } | null
  error: string | null
  /** Reads it again, for the failed row's "Try again". Null after a refusal. */
  reload: (() => void) | null
}

/** The member row as the stored list has it, read again when a fact about this account lands. */
export function useDiscordMember(id: string | null, allowed: boolean): DiscordMemberRead {
  const live = useLiveVersion(
    useCallback((event: LiveEvent) => (id ? concernsPerson(event, id, 'Discord') : false), [id]),
  )

  // A 404 is an answer, not a failure: somebody named in a fact who never was in the server.
  const load = useCallback(
    () =>
      api
        .discordMember(id!)
        // Whether the timeout is still running is worked out when the answer arrives, not on every render.
        .then((member): { member: DiscordMember | null; timedOut: boolean } => ({
          member,
          timedOut: member.timedOutUntil !== null && Date.parse(member.timedOutUntil) > Date.now(),
        }))
        .catch((e: unknown) => {
          if (e instanceof ApiError && e.status === 404) return { member: null, timedOut: false }
          throw e
        }),
    [id],
  )

  const { data, error, reload } = useLoad(id && allowed ? load : null, live)
  return { data: data ?? null, error, reload }
}
