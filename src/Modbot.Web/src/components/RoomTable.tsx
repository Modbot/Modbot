import { dateTime, minutes } from '@/components/charts'
import { WorldLink } from '@/components/facts'
import { openInstance } from '@/lib/subject'
import type { InstanceRow } from '@/lib/api'
import { access } from '@/lib/format'

/**
 * A table of actual rooms: where, when, how busy, and how it ended.
 *
 * One copy, shared by the Instances page's two lists, the rooms in a world, and the rooms one
 * person was seen in. They differ only in which rows they hold.
 *
 * The world's name leads and its id sits underneath rather than replacing it — a moderator
 * matching a row against what they see in game needs the number, and a world Modbot has not read
 * yet has nothing but the id to show. Both the world and the room open their own popup, which is
 * the point: a row that only displayed ids was a dead end.
 */
export function RoomTable({
  rooms,
  showWorld = true,
}: {
  rooms: InstanceRow[]
  /** False inside a world's own popup, where every row is in the same world. */
  showWorld?: boolean
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
        <thead className="text-left text-muted-foreground">
          <tr>
            {showWorld && <th className="py-1 pr-3 font-medium">World</th>}
            <th className="py-1 pr-3 font-medium">Instance</th>
            <th className="py-1 pr-3 text-right font-medium">People</th>
            <th className="py-1 pr-3 text-right font-medium">Most at once</th>
            <th className="py-1 pr-3 text-right font-medium">Open for</th>
            <th className="py-1 font-medium">Started</th>
          </tr>
        </thead>
        <tbody>
          {rooms.map((r) => (
            <tr key={r.id} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
              {showWorld && (
                <td className="py-1 pr-3" title={r.location}>
                  <div className="flex items-center gap-2">
                    {r.worldThumbnailImageUrl && (
                      <img
                        src={r.worldThumbnailImageUrl}
                        alt=""
                        loading="lazy"
                        className="size-8 shrink-0 rounded object-cover"
                      />
                    )}
                    <div className="min-w-0">
                      <div className="truncate">
                        <WorldLink id={r.worldId} name={r.worldName} />
                      </div>
                      <div
                        className="truncate font-mono text-muted-foreground"
                        style={{ fontSize: 'var(--text-tiny, 11px)' }}
                      >
                        {r.worldId}
                      </div>
                    </div>
                  </div>
                </td>
              )}
              <td className="py-1 pr-3">
                <button
                  type="button"
                  onClick={() => openInstance(r.id)}
                  className="rounded-md font-mono font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
                >
                  {r.vrChatInstanceId ?? 'this room'}
                </button>
                <div className="text-muted-foreground" style={{ fontSize: 'var(--text-tiny, 11px)' }}>
                  {[access(r.groupAccessType), r.region?.toUpperCase()].filter(Boolean).join(' · ') || '—'}
                </div>
              </td>
              <td className="py-1 pr-3 text-right tabular-nums">{r.closedAt ? '—' : (r.peopleNow ?? 0)}</td>
              <td className="py-1 pr-3 text-right tabular-nums">{r.peakPeople ?? '—'}</td>
              <td className="py-1 pr-3 text-right tabular-nums">{minutes(r.minutesOpen)}</td>
              <td className="py-1 text-muted-foreground">
                <div>{dateTime(r.openedAt)}</div>
                <div style={{ fontSize: 'var(--text-tiny, 11px)' }}>
                  {!r.closedAt
                    ? 'open now'
                    : r.closedBy === 'time'
                      ? 'went quiet'
                      : `closed ${dateTime(r.closedAt)}`}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
