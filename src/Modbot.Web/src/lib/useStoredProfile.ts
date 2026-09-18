import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, type VRChatUserProfile } from '@/lib/api'
import { useDemo } from '@/lib/demo'

/**
 * One person's stored VRChat profile, brought up to date once on open.
 *
 * Spec 4.2.5: freshness is visible, never implied. Everything read here is shown as of
 * `lastRefreshedAt`, and the parts that draw it label a profile older than the sync's own
 * threshold as stale, because "no flags found" on a bio from March is not the same claim as on a
 * bio from an hour ago.
 *
 * Opening asks the server for a refresh (the "opened in Modbot" tier, behind only people in an
 * instance right now) and then polls the profile until `lastRefreshedAt` moves. Polling is
 * deliberate for this milestone: two seconds with a little backoff, giving up after a minute, no
 * realtime channel. If the refresh cannot happen -- the users lane is cold-stopped, the account is
 * gone -- the stored data stays on screen with the plain reason beside it.
 *
 * A hook rather than a component, because the person popup draws the profile in two places (the
 * identity on the left, the details at the top of the Overview) and the refresh should be asked
 * for once, with both parts moving together when it lands.
 */

/** How long the poll keeps asking after a refresh was queued. */
const GIVE_UP_AFTER_MS = 60_000

/** Poll delays, in order; the last one repeats. */
const POLL_MS = [2_000, 2_000, 3_000, 4_000, 6_000, 8_000]

export type StoredProfile = {
  profile: VRChatUserProfile | null
  error: string | null
  refreshing: boolean
  /** Why the refresh did not happen, or that it is still waiting. */
  note: string | null
  /** For a part that changed the profile itself (the 18+ mark) and has the server's answer. */
  setProfile: (next: VRChatUserProfile) => void
}

/** `version` re-runs the whole read when it changes, which is how a live fact about the person reaches the profile. */
export function useStoredProfile(subjectId: string, version = 0): StoredProfile {
  const demo = useDemo()

  const [profile, setProfile] = useState<VRChatUserProfile | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  // The lastRefreshedAt the refresh was asked against. The poll ends when the server's differs.
  const baseline = useRef<string | null>(null)
  const cancelled = useRef(false)

  const load = useCallback(() => api.userProfile(subjectId), [subjectId])

  useEffect(() => {
    cancelled.current = false
    let timer: ReturnType<typeof setTimeout> | null = null

    const fail = (e: unknown) => {
      if (cancelled.current) return
      setError(
        e instanceof ApiError && e.status === 403
          ? 'You do not have permission to view profiles.'
          : 'Could not load this profile.',
      )
    }

    const poll = (attempt: number, startedAt: number) => {
      timer = setTimeout(() => {
        load()
          .then((next) => {
            if (cancelled.current) return
            setProfile(next)

            const moved = next.lastRefreshedAt !== baseline.current
            const failed =
              next.refreshErrorAt !== null &&
              !next.refresh.pending &&
              next.lastRefreshedAt === baseline.current
            const stopped = !next.refresh.pending && next.refresh.blocked !== null
            const timedOut = Date.now() - startedAt > GIVE_UP_AFTER_MS

            if (moved) {
              setRefreshing(false)
              setNote(null)
              return
            }

            if (failed) {
              setRefreshing(false)
              setNote(`Couldn't refresh: ${next.refreshError ?? 'VRChat did not answer.'}`)
              return
            }

            if (stopped) {
              setRefreshing(false)
              setNote(`Couldn't refresh: ${next.refresh.blocked}`)
              return
            }

            if (timedOut) {
              setRefreshing(false)
              setNote('Refresh still waiting.')
              return
            }

            poll(attempt + 1, startedAt)
          })
          .catch(fail)
      }, POLL_MS[Math.min(attempt, POLL_MS.length - 1)])
    }

    // Show what is stored first, then ask for it to be brought up to date.
    load()
      .then((stored) => {
        if (cancelled.current) return
        setProfile(stored)
        baseline.current = stored.lastRefreshedAt

        // A demo has no VRChat account and never will, so there is nothing to bring the profile
        // up to date from and no refusal worth putting in front of anybody.
        if (demo) return

        if (stored.refresh.blocked && !stored.refresh.pending) {
          setNote(`Couldn't refresh: ${stored.refresh.blocked}`)
          return
        }

        return api.requestUserRefresh(subjectId).then((asked) => {
          if (cancelled.current) return

          if (asked.outcome === 'FreshEnough') {
            setNote(null)
            return
          }

          if (asked.outcome === 'NotAvailable' || asked.outcome === 'NotAPerson') {
            setNote(`Couldn't refresh: ${asked.explanation}`)
            return
          }

          setRefreshing(true)
          poll(0, Date.now())
        })
      })
      .catch(fail)

    return () => {
      cancelled.current = true
      if (timer) clearTimeout(timer)
    }
  }, [subjectId, load, demo, version])

  return { profile, error, refreshing, note, setProfile }
}
