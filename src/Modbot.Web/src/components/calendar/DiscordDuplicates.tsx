import { useCallback } from 'react'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { calendarApi, madeByLabel } from '@/lib/calendar'
import { dateTimeWithWeekday } from '@/lib/format'
import { useLoad } from '@/lib/useLoad'

/**
 * Discord server events, made by anyone, that look like copies of each other (calendar design
 * §16): one block per set of copies, each copy with who made it, and Modbot's own marked. A copy
 * that is one of Modbot's events opens it. Read-only: nothing here changes Discord.
 *
 * Drawn only when there are some, like Drafts: an empty or unread list is not worth a box.
 */
export function DiscordDuplicates({ live, onOpen }: { live: number; onOpen: (eventId: string) => void }) {
  const load = useCallback(() => calendarApi.discordDuplicates(), [])
  const { data } = useLoad(load, live)

  if (!data || data.duplicates.length === 0) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Possible duplicates in Discord</CardTitle>
      </CardHeader>
      <div className="flex flex-col divide-y-(--hairline) divide-border" style={{ fontSize: 'var(--text-small)' }}>
        {data.duplicates.map((d) => (
          <div key={d.copies.map((c) => c.id).join('|')} className="flex flex-col gap-1 px-(--panel-pad) py-2">
            <div className="flex flex-wrap items-baseline gap-x-2">
              <span className="font-medium [overflow-wrap:anywhere]">{d.title}</span>
              <span className="font-mono text-muted-foreground">{dateTimeWithWeekday(d.startsAt)}</span>
            </div>
            <ul className="flex flex-col gap-0.5">
              {d.copies.map((c) => (
                <li key={c.id} className="flex flex-wrap items-baseline gap-x-2">
                  <span className="min-w-0 [overflow-wrap:anywhere]">{c.name}</span>
                  <span className={c.madeBy === 'modbot' ? 'font-medium' : 'text-muted-foreground'}>
                    {c.calendarEventId ? "Modbot's calendar" : madeByLabel(c)}
                  </span>
                  {c.startsAt !== d.startsAt && (
                    <span className="font-mono text-muted-foreground">{dateTimeWithWeekday(c.startsAt)}</span>
                  )}
                  {c.calendarEventId && (
                    <button
                      type="button"
                      className="underline underline-offset-2"
                      onClick={() => onOpen(c.calendarEventId!)}
                    >
                      Open
                    </button>
                  )}
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </Card>
  )
}
