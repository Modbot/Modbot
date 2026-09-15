import { useCallback, useEffect, useState } from 'react'
import {
  api,
  ApiError,
  type DiscordChannel,
  type DiscordChannelPermission,
  type DiscordChannels,
  type DiscordRoles,
} from '@/lib/api'

/**
 * The Discord server's channel and role lists, shared by every picker on a page.
 *
 * Two pickers on one form should cost one request, not two, so the answer is kept per list and
 * handed to each caller. Opening a picker asks again, so a channel made a minute ago shows up
 * without reloading the page.
 */
function sharedLoad<T>(load: () => Promise<T>): (fresh: boolean) => Promise<T> {
  let pending: Promise<T> | null = null

  return (fresh) => {
    if (!pending || fresh) {
      const next = load()
      pending = next
      // A failure is not kept: the next caller tries again.
      next.catch(() => {
        if (pending === next) pending = null
      })
    }
    return pending
  }
}

const channels = sharedLoad(() => api.discordChannels())
const roles = sharedLoad(() => api.discordRoles())

function useShared<T>(get: (fresh: boolean) => Promise<T>) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    (fresh: boolean) => {
      get(fresh)
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to see this.'
              : 'Could not load the list.',
          ),
        )
    },
    [get],
  )

  useEffect(() => {
    load(false)
  }, [load])

  const reload = useCallback(() => load(true), [load])

  return { data, error, reload }
}

export function useDiscordChannels(): {
  data: DiscordChannels | null
  error: string | null
  reload: () => void
} {
  return useShared(channels)
}

export function useDiscordRoles(): { data: DiscordRoles | null; error: string | null; reload: () => void } {
  return useShared(roles)
}

/** What a channel events are sent to needs: posts are embeds. */
export const EVENT_POST_NEEDS: readonly DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks']

/** Discord's own names for the permissions, as a moderator sees them in Discord's settings. */
export const channelPermissionNames: Record<DiscordChannelPermission, string> = {
  viewChannel: 'View Channel',
  readMessageHistory: 'Read Message History',
  sendMessages: 'Send Messages',
  embedLinks: 'Embed Links',
  attachFiles: 'Attach Files',
  manageMessages: 'Manage Messages',
}

/** The permissions from `needs` the bot lacks in this channel, by Discord's names. */
export function missingPermissions(channel: DiscordChannel, needs: readonly DiscordChannelPermission[]): string[] {
  return needs.filter((p) => !channel.botPermissions[p]).map((p) => channelPermissionNames[p])
}

/** `Missing Send Messages, Embed Links`, or null when nothing is missing. */
export function missingLabel(channel: DiscordChannel, needs: readonly DiscordChannelPermission[]): string | null {
  const missing = missingPermissions(channel, needs)
  return missing.length ? `Missing ${missing.join(', ')}` : null
}
