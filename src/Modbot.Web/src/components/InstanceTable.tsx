import { minutes } from '@/components/charts'
import { RegionBadge } from '@/components/InstanceBadges'
import { WorldLink } from '@/components/facts'
import { openInstance } from '@/lib/subject'
import type { InstanceRow } from '@/lib/api'
import { accessInGame, dateTime, whenRange } from '@/lib/format'
import { instanceEnd, instanceNumber } from '@/lib/instanceName'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { cn } from '@/lib/utils'
import { HeadCount } from '@/components/HeadCount'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'

/**
 * A table of actual instances: where, when, how busy, and how it ended. "closed" only when a
 * moderator closed it by hand; an instance that emptied out and dropped off the list "ended".
 *
 * One copy, shared by the Instances page's two lists, the instances in a world, and the instances one
 * person was seen in. They differ only in which rows they hold.
 *
 * The world is its name alone. Its id is in the tooltip and the world's popup, and printed under
 * every name it was a column of noise nobody read; a world Modbot has not read yet shows the id in
 * place of the name, because that is all there is. Both the world and the instance open their own
 * popup, which is the point: a row that only displayed ids was a dead end. An instance opened with
 * a name shows the name, and its number moves to the tooltip.
 *
 * The whole row opens the instance; the world's name still opens the world. Who could join is said
 * in the game's words and the region with the tile's flag, so a row and the game read the same.
 * One "When" range replaces a start and an end stacked in one cell, and there is no "people now"
 * column: it was a dash on every row but the open one, which says how many are there instead.
 *
 * On a phone the five columns were 689 px, and only the world fitted. There each instance is a
 * two-line row: the world and the most people at once, then the instance, how long it was open and
 * when it began ("open now" and how many are there, for one still open). Who could join, the region
 * and when it ended are in the instance's popup, which the row opens.
 */
export function InstanceTable({
  instances,
  showWorld = true,
}: {
  instances: InstanceRow[]
  /** False inside a world's own popup, where every row is in the same world. */
  showWorld?: boolean
}) {
  return (
    <Table
      pinFirst
      narrow={
        <NarrowRows>
          {instances.map((r) => (
            <NarrowRow
              key={r.id}
              onOpen={() => openInstance(r.id)}
              hasLinks={showWorld}
              picture={
                showWorld &&
                r.worldThumbnailImageUrl && (
                  <img
                    src={vrchatMedia(r.worldThumbnailImageUrl)}
                    alt=""
                    loading="lazy"
                    className="size-10 shrink-0 object-cover"
                  />
                )
              }
              main={
                <span className="block truncate">
                  {showWorld ? (
                    <WorldLink id={r.worldId} name={r.worldName} unnamed="id" />
                  ) : (
                    <InstanceName instance={r} />
                  )}
                </span>
              }
              side={
                r.peakPeople !== null && (
                  <span className="font-mono font-medium">
                    <HeadCount count={r.peakPeople} unsure={r.peakPeopleUnsure} />
                  </span>
                )
              }
              facts={[
                showWorld && <InstanceName key="instance" instance={r} />,
                <span key="open" className="font-mono">
                  {minutes(r.minutesOpen)}
                </span>,
                r.closedAt ? (
                  <span key="began" className="font-mono">
                    {dateTime(r.openedAt)}
                  </span>
                ) : (
                  'open now'
                ),
                !r.closedAt && (
                  <span key="here">
                    <span className="font-mono">
                      <HeadCount count={r.peopleNow ?? 0} unsure={r.peopleNow !== null && r.peopleNowUnsure} />
                    </span>{' '}
                    here
                  </span>
                ),
              ]}
            />
          ))}
        </NarrowRows>
      }
      head={
        <>
          {showWorld && <Th>World</Th>}
          <Th>Instance</Th>
          <Th className="text-right">Most at once</Th>
          <Th className="text-right">Open for</Th>
          <Th>When</Th>
        </>
      }
    >
      {instances.map((r) => (
        <Tr
          key={r.id}
          className="cursor-pointer hover:bg-muted/40"
          onClick={(e) => {
            // The world's name and the instance's own button open what they name.
            if ((e.target as HTMLElement).closest('button, a')) return
            openInstance(r.id)
          }}
        >
          {showWorld && (
            <Td title={r.location}>
              <div className="flex items-center gap-2">
                {r.worldThumbnailImageUrl && (
                  <img
                    src={vrchatMedia(r.worldThumbnailImageUrl)}
                    alt=""
                    loading="lazy"
                    className="size-8 shrink-0 object-cover"
                  />
                )}
                <div className="min-w-0 truncate">
                  <WorldLink id={r.worldId} name={r.worldName} unnamed="id" />
                </div>
              </div>
            </Td>
          )}
          <Td>
            <button
              type="button"
              onClick={() => openInstance(r.id)}
              title={r.instanceName?.trim() && r.vrChatInstanceId ? instanceNumber(r.vrChatInstanceId) : undefined}
              className={cn(
                'rounded-sm font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring',
                !r.instanceName?.trim() && 'font-mono',
              )}
            >
              {instanceNumber(r.vrChatInstanceId, r.instanceName)}
            </button>
            {/* "Group members · EU" over three lines makes every row in the table three lines
                tall on a phone. The table already scrolls; the row need not also be a stack. */}
            <div
              className="flex items-center gap-1.5 whitespace-nowrap text-muted-foreground"
              style={{ fontSize: 'var(--text-tiny)' }}
            >
              {accessInGame(r.groupAccessType) ?? (r.region ? null : '—')}
              {accessInGame(r.groupAccessType) && r.region && <span aria-hidden>·</span>}
              <RegionBadge region={r.region} />
            </div>
          </Td>
          <Td className="text-right font-mono">
            {r.peakPeople === null ? '—' : <HeadCount count={r.peakPeople} unsure={r.peakPeopleUnsure} />}
          </Td>
          <Td className="text-right font-mono">{minutes(r.minutesOpen)}</Td>
          <Td className="whitespace-nowrap text-muted-foreground">
            <div className="font-mono">{whenRange(r.openedAt, r.closedAt)}</div>
            <div style={{ fontSize: 'var(--text-tiny)' }}>
              {!r.closedAt ? (
                <>
                  open now ·{' '}
                  <span className="font-mono">
                    <HeadCount count={r.peopleNow ?? 0} unsure={r.peopleNow !== null && r.peopleNowUnsure} />
                  </span>{' '}
                  here
                </>
              ) : (
                instanceEnd(r)
              )}
            </div>
          </Td>
        </Tr>
      ))}
    </Table>
  )
}

/** An instance on a phone row: its name, or its number when it has none, as plain text -- the row opens it. */
function InstanceName({ instance: r }: { instance: InstanceRow }) {
  return (
    <span className={cn(!r.instanceName?.trim() && 'font-mono')}>
      {instanceNumber(r.vrChatInstanceId, r.instanceName)}
    </span>
  )
}
