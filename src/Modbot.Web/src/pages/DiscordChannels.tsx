import { useEffect, useMemo, useState } from 'react'
import { ArrowDown, ArrowUp, ArrowUpDown, Lock } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { FilterBar } from '@/components/filters/FilterBar'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Unread } from '@/components/Freshness'
import { Empty } from '@/components/ListParts'
import {
  api,
  ApiError,
  type CurrentUser,
  type QuietChannelList,
  type QuietChannelQuery,
  type QuietChannelRow,
} from '@/lib/api'
import {
  CHANNEL_SORT_LABELS,
  CHANNEL_STATUS_LABELS,
  CHANNEL_TYPE_LABELS,
  NO_CATEGORY,
  QUIET_DAYS,
  channelQueryFrom,
  channelSortParam,
  filterChannels,
  firstDirection,
  quietFor,
  readChannelSort,
  sortChannels,
  type ChannelSort,
  type ChannelSortKey,
} from '@/lib/discordTidyUp'
import { useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { formatDay } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { useQueryParam } from '@/lib/router'
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
 * Staff-only channels -- ones @everyone cannot see -- carry Discord's lock. Their names are sent only
 * to someone who can also change settings; everybody else gets the public channels and a count of
 * the staff-only ones left out.
 *
 * Every column sorts, and the filters are the app's filter chips. Only "Seen by" a role goes to the
 * server, which holds the channels' overwrites; the rest narrow the rows already here. The sort and
 * the chips live in the address, so a link reproduces the view (lib/filters.ts).
 */
export function DiscordChannels({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const server = useServerProfile(me)

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-channels" />}
      <ChannelList me={me} />
    </div>
  )
}

/** Nothing filtered: every channel, which is what the tab opens on. */
const CHANNEL_DEFAULTS: FilterChip[] = []
const CHANNEL_STARTS = [CHANNEL_DEFAULTS]

function ChannelList({ me }: { me: CurrentUser }) {
  const [chips, setChips] = useFilters(CHANNEL_DEFAULTS)
  const [sortParam, setSortParam] = useQueryParam('sort')
  const sort = readChannelSort(sortParam)
  const [search, setSearch] = useState('')

  // The chips change identity whenever the address does, sort clicks included; only a different
  // question for the server is a reason to ask it again.
  const queryKey = JSON.stringify(channelQueryFrom(chips))
  const query = useMemo(() => JSON.parse(queryKey) as QuietChannelQuery, [queryKey])

  const [list, setList] = useState<QuietChannelList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorFinal, setErrorFinal] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .discordQuietChannels(query)
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
  }, [query, tries])

  const seesStaffOnly = can(me, 'ManageSettings')

  const properties = useMemo<FilterProperty[]>(
    () => [
      {
        id: 'seenBy',
        label: 'Seen by',
        kind: 'choice',
        options: (list?.roles ?? []).map((r) => ({
          value: r.id,
          label: r.name,
          color: r.color === 0 ? null : `#${r.color.toString(16).padStart(6, '0')}`,
        })),
      },
      {
        id: 'quiet',
        label: 'Quiet for',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: QUIET_DAYS.map((days) => ({ value: String(days), label: `${days} days or more` })),
      },
      {
        id: 'category',
        label: 'Category',
        kind: 'choice',
        options: [
          { value: NO_CATEGORY, label: 'No category' },
          ...(list?.categories ?? []).map((c) => ({ value: c.id, label: c.name })),
        ],
      },
      {
        id: 'type',
        label: 'Type',
        kind: 'choice',
        options: Object.entries(CHANNEL_TYPE_LABELS).map(([value, label]) => ({ value, label })),
      },
      {
        id: 'status',
        label: 'Status',
        kind: 'choice',
        options: Object.entries(CHANNEL_STATUS_LABELS).map(([value, label]) => ({ value, label })),
      },
      ...(seesStaffOnly ? [{ id: 'staffOnly', label: 'Staff-only', kind: 'yesno' as const }] : []),
    ],
    [list?.roles, list?.categories, seesStaffOnly],
  )

  const sortBy = (next: ChannelSort) => setSortParam(channelSortParam(next))

  // A click on the column in use turns it round; a click on another starts it the way it reads best.
  const clickColumn = (key: ChannelSortKey) =>
    sortBy(sort.key === key ? { key, dir: sort.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: firstDirection(key) })

  const bar = (
    <FilterBar properties={properties} chips={chips} starts={CHANNEL_STARTS} onChange={setChips}>
      <Input
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        placeholder="Search by name"
        className="w-56"
        aria-label="Search Discord channels"
      />

      <Select value={sort.key} onChange={(v) => sortBy({ key: v as ChannelSortKey, dir: firstDirection(v as ChannelSortKey) })} aria-label="Sort">
        {Object.entries(CHANNEL_SORT_LABELS).map(([value, label]) => (
          <option key={value} value={value}>
            {label}
          </option>
        ))}
      </Select>
      <Button
        variant="outline"
        size="icon"
        aria-label={sort.dir === 'asc' ? 'Ascending' : 'Descending'}
        onClick={() => sortBy({ key: sort.key, dir: sort.dir === 'asc' ? 'desc' : 'asc' })}
      >
        {sort.dir === 'asc' ? <ArrowUp aria-hidden /> : <ArrowDown aria-hidden />}
      </Button>
    </FilterBar>
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

  const channels = sortChannels(filterChannels(list.channels, chips, search, list.now), sort, list.now)

  return (
    <div className="flex flex-col gap-3">
      {bar}

      <Card>
        <CardHeader className={cn(list.guildId === null && 'bg-warn/10')}>
          {list.guildId === null && <Unread>No Discord server set.</Unread>}
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {list.staffOnlyHidden > 0 && (
              <span className="mr-3 inline-flex items-center gap-1">
                <Lock className="size-[0.85em]" aria-hidden />
                <span className="font-mono">{list.staffOnlyHidden.toLocaleString()}</span>{' '}
                {list.staffOnlyHidden === 1 ? 'staff-only channel' : 'staff-only channels'}
              </span>
            )}
            <span className="font-mono">{channels.length.toLocaleString()}</span>
            {channels.length !== list.channels.length && (
              <>
                {' of '}
                <span className="font-mono">{list.channels.length.toLocaleString()}</span>
              </>
            )}{' '}
            {list.channels.length === 1 ? 'channel' : 'channels'}
          </span>
        </CardHeader>

        {channels.length === 0 ? (
          <EmptyRow>No channels</EmptyRow>
        ) : (
          <Table
            pinFirst
            narrow={
              <NarrowRows>
                {channels.map((channel) => (
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
                {(['name', 'category', 'last', 'quiet'] as const).map((key) => (
                  <SortTh key={key} column={key} sort={sort} onSort={clickColumn} />
                ))}
              </>
            }
          >
            {channels.map((channel) => (
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

/** A column name that sorts the table when clicked, with an arrow on the column in use. */
function SortTh({
  column,
  sort,
  onSort,
}: {
  column: ChannelSortKey
  sort: ChannelSort
  onSort: (column: ChannelSortKey) => void
}) {
  const active = sort.key === column
  const Arrow = !active ? ArrowUpDown : sort.dir === 'asc' ? ArrowUp : ArrowDown

  return (
    <Th aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : undefined}>
      <button
        type="button"
        onClick={() => onSort(column)}
        className={cn('inline-flex cursor-pointer items-center gap-1 hover:text-foreground', active && 'text-foreground')}
      >
        {CHANNEL_SORT_LABELS[column]}
        <Arrow className={cn('size-3', !active && 'opacity-40')} aria-hidden />
      </button>
    </Th>
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
