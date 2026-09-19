import { useEffect, useState } from 'react'
import { changesBans, changesMembers } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'

/** A ban, a kick or a removal: what moves somebody up this list. */
const changesOffenders = (event: Parameters<typeof changesBans>[0]) => changesBans(event) || changesMembers(event)
import { Card, CardContent } from '@/components/ui/card'
import { SubjectLink } from '@/components/facts'
import { StatusPill } from '@/components/SubjectHistory'
import { ago, formatDay } from '@/lib/format'
import { api, ApiError, type RepeatOffenderList } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * People acted on more than once (spec 5.8.4), most recent action first.
 *
 * A tab component rather than a page, so the Bans page -- or the Members page once member sync
 * lands -- can mount it next to its own list. Everything here comes from the repeat-offender
 * counts the detection run rebuilds from the fact log, so the tab says when that last happened,
 * and the status rule is printed above the table rather than hidden in a tooltip.
 */
export function RepeatOffendersTab({ onOpenSubject }: { onOpenSubject: (id: string) => void }) {
  const [list, setList] = useState<RepeatOffenderList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<'all' | 'repeat'>('all')

  // Read again when the live stream says somebody was acted on: a ban, a kick, a removal.
  const live = useLiveVersion(changesOffenders)

  useEffect(() => {
    let cancelled = false

    api
      .repeatOffenders({ status, limit: 200 })
      .then((next) => {
        if (!cancelled) {
          setList(next)
          setError(null)
        }
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see people’s histories.'
            : 'Could not load the list.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [status, live])

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  if (!list) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <div
        className="rounded-xl border bg-muted/40 px-4 py-3 text-muted-foreground"
        style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
      >
        <p>{list.rule}</p>
        <p className="mt-1">
          {list.lastRunAt ? `Counts rebuilt ${ago(list.lastRunAt, list.now)}.` : 'Counts not built yet.'}
        </p>
      </div>

      <Card>
        <CardContent className="p-0">
          <div
            className="flex flex-wrap items-center gap-3 border-b px-3 py-2"
            style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          >
            <span className="font-medium">
              {list.total} {list.total === 1 ? 'person' : 'people'} acted on more than once
            </span>
            <span className="flex-1" />
            <div role="group" className="flex gap-0.5 rounded-md border bg-secondary p-0.5" style={{ borderWidth: 'var(--hairline)' }}>
              {(
                [
                  { value: 'all', label: 'Everyone' },
                  { value: 'repeat', label: 'Repeat only' },
                ] as const
              ).map((o) => (
                <button
                  key={o.value}
                  type="button"
                  aria-pressed={status === o.value}
                  onClick={() => setStatus(o.value)}
                  className={cn(
                    'rounded-md px-2 font-medium transition-colors',
                    status === o.value ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
                  )}
                  style={{ height: 'calc(var(--control-h) - 8px)' }}
                >
                  {o.label}
                </button>
              ))}
            </div>
          </div>

          {list.people.length === 0 ? (
            <div className="py-10 text-center text-muted-foreground">
              <div className="font-medium text-foreground">Nobody yet</div>
            </div>
          ) : (
            <div data-pin-first className="relative overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">Person</th>
                    <th className="px-3 py-2 text-left font-normal">Status</th>
                    <th className="px-3 py-2 text-right font-normal">Actions</th>
                    <th className="px-3 py-2 text-right font-normal">Last 30 days</th>
                    <th className="px-3 py-2 text-right font-normal">Kicks</th>
                    <th className="px-3 py-2 text-right font-normal">Warns</th>
                    <th className="px-3 py-2 text-right font-normal">Bans</th>
                    <th className="px-3 py-2 text-right font-normal">Removed</th>
                    <th className="px-3 py-2 text-right font-normal">Turned away</th>
                    <th className="px-3 py-2 text-right font-normal">Moderators</th>
                    <th className="px-3 py-2 text-left font-normal">Last action</th>
                  </tr>
                </thead>
                <tbody>
                  {list.people.map((p) => (
                    <tr
                      key={`${p.who.platform}:${p.who.id}`}
                      className="border-b last:border-0 hover:bg-muted/40"
                      style={{ borderBottomWidth: 'var(--hairline)' }}
                    >
                      <td className="px-3" style={{ height: 'var(--row-h)' }}>
                        <SubjectLink id={p.who.id} name={p.who.name} onOpen={onOpenSubject} />
                      </td>
                      <td className="px-3">
                        <StatusPill status={p.status} />
                      </td>
                      <Num n={p.actions} strong />
                      <Num n={p.actionsLast30Days} />
                      <Num n={p.instanceKicks} />
                      <Num n={p.warns} />
                      <Num n={p.bans} />
                      <Num n={p.removals} />
                      <Num n={p.rejections} />
                      <Num n={p.moderators} />
                      <td className="px-3 text-muted-foreground">
                        <div>
                          {p.lastActionLabel}
                          {p.lastBy && (
                            <>
                              {' '}by <SubjectLink id={p.lastBy.id} name={p.lastBy.name} onOpen={onOpenSubject} />
                            </>
                          )}
                        </div>
                        <div className="text-muted-foreground/70" title={formatDay(p.lastActionAt)}>
                          {ago(p.lastActionAt, list.now)}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function Num({ n, strong }: { n: number; strong?: boolean }) {
  return (
    <td className={cn('px-3 text-right tabular-nums', strong ? 'font-medium' : 'text-muted-foreground')}>
      {n === 0 ? '·' : n.toLocaleString()}
    </td>
  )
}
