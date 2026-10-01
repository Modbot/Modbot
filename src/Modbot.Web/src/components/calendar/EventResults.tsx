import { useCallback, type ReactNode } from 'react'
import { compactNumber, minutes } from '@/components/charts'
import { HeadCount } from '@/components/HeadCount'
import { InstanceLink, SubjectLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { calendarApi, type CalendarEvent } from '@/lib/calendar'
import { openSubject } from '@/lib/subject'
import { useLoad } from '@/lib/useLoad'

/** How many of the people seen are named under the figures; the rest are in the instance's People tab. */
const NAMED = 8

/**
 * What one time of an event did, under the event when that time has started: the instance's most at
 * once and how long it ran, the group's new members and join requests, and, with See the audit log,
 * who a moderator's client saw there. Each figure has the middle value of the event's earlier times
 * beside it, once it has some (`CalendarResults` on the server says where each number comes from).
 *
 * The instance is named only when it is not the one the event's own Instance line above already
 * links: that line is about the time Modbot is dealing with now, and this one may be an earlier time.
 */
export function EventResults({ event, start, live }: { event: CalendarEvent; start: Date; live: number }) {
  const load = useCallback(() => calendarApi.results(event.id, start), [event.id, start])
  const { data, error } = useLoad(load, live)

  if (!data)
    return (
      <Section title="How it went">
        {error ? <span className="text-destructive">{error}</span> : <span className="text-muted-foreground">Loading…</span>}
      </Section>
    )

  const { occurrence, usual, people } = data
  const instance = occurrence.instance
  const seen = occurrence.seen
  const compare = usual !== null

  return (
    <>
      <Section title="How it went">
        {instance && instance.id !== event.opening?.instanceId && (
          <div>
            <span className="text-muted-foreground">Instance </span>
            <InstanceLink
              modbotInstanceId={instance.id}
              worldId={instance.worldId}
              worldName={instance.worldName}
              number={instance.vrChatInstanceId}
              name={instance.instanceName}
            />
          </div>
        )}
        <Figures compare={compare}>
          <Figure
            label="Most at once"
            value={
              instance?.peakPeople == null ? '—' : <HeadCount count={instance.peakPeople} unsure={instance.peakPeopleUnsure} />
            }
            usual={usual?.peakPeople == null ? '—' : compactNumber(usual.peakPeople)}
            compare={compare}
          />
          <Figure
            label={instance && !instance.closedAt ? 'Open for' : 'Ran for'}
            value={instance ? minutes(instance.minutesOpen) : '—'}
            usual={usual?.minutesOpen == null ? '—' : minutes(usual.minutesOpen)}
            compare={compare}
          />
          <Figure
            label="New members"
            value={compactNumber(occurrence.newMembers)}
            usual={usual ? compactNumber(usual.newMembers) : '—'}
            compare={compare}
          />
          <Figure
            label="Join requests"
            value={compactNumber(occurrence.joinRequests)}
            usual={usual ? compactNumber(usual.joinRequests) : '—'}
            compare={compare}
          />
        </Figures>
      </Section>

      {data.canSeeWhoWasThere && instance && seen && (
        <Section title="Seen by a moderator's client">
          {seen.visitors === 0 ? (
            <span className="text-muted-foreground">Nobody seen.</span>
          ) : (
            <>
              <Figures compare={compare}>
                <Figure
                  label="People"
                  value={compactNumber(seen.visitors)}
                  usual={usual?.peopleSeen == null ? '—' : compactNumber(usual.peopleSeen)}
                  compare={compare}
                />
                <Figure
                  label="People-time"
                  value={minutes(seen.minutesSeen)}
                  usual={usual?.minutesSeen == null ? '—' : minutes(usual.minutesSeen)}
                  compare={compare}
                />
              </Figures>
              <ul className="flex flex-wrap items-center gap-1.5">
                {people.slice(0, NAMED).map((p) => (
                  <li key={p.userId}>
                    <Badge variant="outline" className="gap-1.5 text-foreground">
                      <SubjectLink id={p.userId} name={p.displayName} />
                      <span className="font-mono text-muted-foreground">{minutes(p.minutesSeen)}</span>
                    </Badge>
                  </li>
                ))}
                {people.length > NAMED && (
                  <li>
                    <button
                      type="button"
                      className="text-muted-foreground hover:text-foreground hover:underline"
                      onClick={() => openSubject({ kind: 'instance', id: instance.id }, { tab: 'people' })}
                    >
                      Everybody
                    </button>
                  </li>
                )}
              </ul>
            </>
          )}
        </Section>
      )}
    </>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5 border-t border-t-(length:--hairline) pt-3">
      <div className="font-label">{title}</div>
      {children}
    </div>
  )
}

/** Label, figure and, once there are earlier times, the usual figure, in three lined-up columns. */
function Figures({ compare, children }: { compare: boolean; children: ReactNode }) {
  return (
    <div className={compare ? 'grid grid-cols-[1fr_auto_auto] gap-x-3 gap-y-1' : 'grid grid-cols-[1fr_auto] gap-x-3 gap-y-1'}>
      {compare && (
        <span className="col-start-3 text-right text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
          Usually
        </span>
      )}
      {children}
    </div>
  )
}

function Figure({ label, value, usual, compare }: { label: string; value: ReactNode; usual: ReactNode; compare: boolean }) {
  return (
    <>
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right font-mono">{value}</span>
      {compare && <span className="text-right font-mono text-muted-foreground">{usual}</span>}
    </>
  )
}
