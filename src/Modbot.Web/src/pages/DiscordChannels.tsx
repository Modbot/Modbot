import { useEffect, useState } from 'react'
import { Lock } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Select } from '@/components/ui/select'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Unread } from '@/components/Freshness'
import { Empty } from '@/components/ListParts'
import { api, ApiError, type CurrentUser, type QuietChannelList, type QuietChannelRow } from '@/lib/api'
import { quietFor } from '@/lib/discordTidyUp'
import { formatDay } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { channelLook } from '@/lib/serverOverview'
import { isFinal } from '@/lib/tryAgain'
import { cn } from '@/lib/utils'
import { ServerHeader } from '@/pages/analytics/ServerHeader'
import { ChannelIcon } from '@/pages/analytics/ServerStats'
import { useServerProfile } from '@/pages/analytics/useServerProfile'

/**
 * The Discord page's Channels tab: the server's text, announcement and forum channels, quietest
 * first, with when anybody last wrote in each (Discord tidy-up design). Read only: nothing here
 * archives or deletes a channel; tidying up is done in Discord.
 *
 * The last message is the newest Modbot has stored for the channel or any of its threads, so the
 * page costs no request to Discord. How long it has been is worked out against the server's clock.
 * Staff-only channels -- ones @everyone cannot see -- carry Discord's lock and can be left out.
 */
export function DiscordChannels({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const server = useServerProfile(me)

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-channels" />}
      <ChannelList />
    </div>
  )
}

type Shown = 'all' | 'hide-staff-only'

function ChannelList() {
  const [shown, setShown] = useState<Shown>('all')
  const [list, setList] = useState<QuietChannelList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorFinal, setErrorFinal] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .discordQuietChannels(shown === 'hide-staff-only')
      .then((next) => {
        if (cancelled) return
        setList(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setErrorFinal(isFinal(e))
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view analytics.'
            : 'Could not load the Discord channels.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [shown, tries])

  const bar = (
    <div className="flex flex-wrap items-center gap-2">
      <Select value={shown} onChange={(v) => setShown(v as Shown)} aria-label="Channels shown">
        <option value="all">All channels</option>
        <option value="hide-staff-only">Hide staff-only</option>
      </Select>
    </div>
  )

  if (error || !list) {
    return (
      <div className="flex flex-col gap-3">
        {bar}
        <Empty
          tone={error ? 'danger' : 'loading'}
          onTryAgain={
            errorFinal
              ? null
              : () => {
                  setError(null)
                  setTries((n) => n + 1)
                }
          }
        >
          {error}
        </Empty>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      {bar}

      <Card>
        <CardHeader className={cn(list.guildId === null && 'bg-warn/10')}>
          {list.guildId === null && <Unread>No Discord server set.</Unread>}
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-mono">{list.channels.length.toLocaleString()}</span>{' '}
            {list.channels.length === 1 ? 'channel' : 'channels'}
          </span>
        </CardHeader>

        {list.channels.length === 0 ? (
          <EmptyRow>No channels</EmptyRow>
        ) : (
          <Table
            pinFirst
            narrow={
              <NarrowRows>
                {list.channels.map((channel) => (
                  <NarrowRow
                    key={channel.id}
                    main={<ChannelName channel={channel} />}
                    side={<Quiet channel={channel} now={list.now} />}
                    facts={[channel.categoryName, channel.lastMessageAt && formatDay(channel.lastMessageAt)]}
                  />
                ))}
              </NarrowRows>
            }
            head={
              <>
                <Th>Channel</Th>
                <Th>Category</Th>
                <Th>Last message</Th>
                <Th>Quiet for</Th>
              </>
            }
          >
            {list.channels.map((channel) => (
              <Tr key={channel.id}>
                <Td>
                  <ChannelName channel={channel} />
                </Td>
                <Td className="text-muted-foreground">{channel.categoryName ?? '—'}</Td>
                <Td className="font-mono">
                  {channel.lastMessageAt ? (
                    formatDay(channel.lastMessageAt)
                  ) : (
                    <span className="text-muted-foreground">—</span>
                  )}
                </Td>
                <Td>
                  <Quiet channel={channel} now={list.now} />
                </Td>
              </Tr>
            ))}
          </Table>
        )}
      </Card>
    </div>
  )
}

/** The channel's picture and name, as Discord's list draws them, with its lock when staff-only. */
function ChannelName({ channel }: { channel: QuietChannelRow }) {
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      <ChannelIcon look={channelLook(channel.type)} />
      <span className="truncate font-medium">{channel.name}</span>
      {channel.staffOnly && <Lock className="size-[0.85em] shrink-0 text-muted-foreground" aria-label="Staff-only" />}
    </span>
  )
}

function Quiet({ channel, now }: { channel: QuietChannelRow; now: string }) {
  return (
    <span className={cn(channel.lastMessageAt ? 'font-mono' : 'text-muted-foreground', 'whitespace-nowrap')}>
      {quietFor(channel, now)}
    </span>
  )
}
