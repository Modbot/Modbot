import { dateTime, minutes } from '@/components/charts'
import { WorldLink } from '@/components/facts'
import { openInstance } from '@/lib/subject'
import type { InstanceRow } from '@/lib/api'
import { access } from '@/lib/format'
import { instanceNumber } from '@/lib/instanceName'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { cn } from '@/lib/utils'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'

/**
 * A table of actual instances: where, when, how busy, and how it ended.
 *
 * One copy, shared by the Instances page's two lists, the instances in a world, and the instances one
 * person was seen in. They differ only in which rows they hold.
 *
 * The world is its name alone. Its id is in the tooltip and the world's popup, and printed under
 * every name it was a column of noise nobody read; a world Modbot has not read yet shows the id in
 * place of the name, because that is all there is. Both the world and the instance open their own
 * popup, which is the point: a row that only displayed ids was a dead end. An instance opened with
 * a name shows the name, and its number moves to the tooltip.
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
      head={
        <>
          {showWorld && <Th>World</Th>}
          <Th>Instance</Th>
          <Th className="text-right">People</Th>
          <Th className="text-right">Most at once</Th>
          <Th className="text-right">Open for</Th>
          <Th>Started</Th>
        </>
      }
    >
      {instances.map((r) => (
        <Tr key={r.id}>
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
            <div className="whitespace-nowrap text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
              {[access(r.groupAccessType), r.region?.toUpperCase()].filter(Boolean).join(' · ') || '—'}
            </div>
          </Td>
          <Td className="text-right font-mono">{r.closedAt ? '—' : (r.peopleNow ?? 0)}</Td>
          <Td className="text-right font-mono">{r.peakPeople ?? '—'}</Td>
          <Td className="text-right font-mono">{minutes(r.minutesOpen)}</Td>
          <Td className="whitespace-nowrap text-muted-foreground">
            <div className="font-mono">{dateTime(r.openedAt)}</div>
            <div style={{ fontSize: 'var(--text-tiny)' }}>
              {!r.closedAt
                ? 'open now'
                : r.closedBy === 'time'
                  ? 'went quiet'
                  : <>closed <span className="font-mono">{dateTime(r.closedAt)}</span></>}
            </div>
          </Td>
        </Tr>
      ))}
    </Table>
  )
}
