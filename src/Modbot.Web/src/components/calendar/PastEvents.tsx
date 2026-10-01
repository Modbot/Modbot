import { useCallback } from 'react'
import { compactNumber, minutes } from '@/components/charts'
import { HeadCount } from '@/components/HeadCount'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { calendarApi, type CalendarOccurrenceResult } from '@/lib/calendar'
import { dateTimeWithWeekday, plural } from '@/lib/format'
import { useLoad } from '@/lib/useLoad'

/**
 * Every time an event ran over the last 90 days, the most at once first, under the calendar:
 * which events are worth running. A row opens that time of the event, with what it did.
 *
 * Drawn only once it has rows, like Drafts above it: the calendar is the page, and an empty or
 * failed list is not worth a box under it.
 */
export function PastEvents({
  live,
  onOpen,
}: {
  live: number
  onOpen: (result: CalendarOccurrenceResult) => void
}) {
  const load = useCallback(() => calendarApi.past(), [])
  const { data } = useLoad(load, live)

  if (!data || data.occurrences.length === 0) return null

  const rows = data.occurrences
  const key = (r: CalendarOccurrenceResult) => `${r.eventId}|${r.startsAt}`

  return (
    <Card>
      <CardHeader>
        <CardTitle>Past events</CardTitle>
      </CardHeader>
      <Table
        pinFirst
        narrow={
          <NarrowRows>
            {rows.map((r) => (
              <NarrowRow
                key={key(r)}
                onOpen={() => onOpen(r)}
                main={<span className="block truncate">{r.title}</span>}
                side={
                  r.instance?.peakPeople != null && (
                    <span className="font-mono">
                      <HeadCount count={r.instance.peakPeople} unsure={r.instance.peakPeopleUnsure} />
                    </span>
                  )
                }
                facts={[
                  <span key="when" className="font-mono">
                    {dateTimeWithWeekday(r.startsAt)}
                  </span>,
                  <span key="members" className="font-mono">
                    {`${compactNumber(r.newMembers)} new ${plural(r.newMembers, 'member')}`}
                  </span>,
                ]}
              />
            ))}
          </NarrowRows>
        }
        head={
          <>
            <Th>Event</Th>
            <Th>When</Th>
            <Th className="text-right">Most at once</Th>
            <Th className="text-right">Ran for</Th>
            <Th className="text-right">New members</Th>
            <Th className="text-right">Join requests</Th>
          </>
        }
      >
        {rows.map((r) => (
          <Tr key={key(r)} className="cursor-pointer hover:bg-muted/40" onClick={() => onOpen(r)}>
            <Td className="max-w-[20rem] truncate">{r.title}</Td>
            <Td className="font-mono text-muted-foreground">{dateTimeWithWeekday(r.startsAt)}</Td>
            <Td className="text-right font-mono">
              {r.instance?.peakPeople == null ? (
                '—'
              ) : (
                <HeadCount count={r.instance.peakPeople} unsure={r.instance.peakPeopleUnsure} />
              )}
            </Td>
            <Td className="text-right font-mono">{r.instance ? minutes(r.instance.minutesOpen) : '—'}</Td>
            <Td className="text-right font-mono">{compactNumber(r.newMembers)}</Td>
            <Td className="text-right font-mono">{compactNumber(r.joinRequests)}</Td>
          </Tr>
        ))}
      </Table>
    </Card>
  )
}
