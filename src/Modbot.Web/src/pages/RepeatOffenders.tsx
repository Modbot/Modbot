import { useEffect, useState } from 'react'
import { changesBans, changesMembers } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'

/** A ban, a kick or a removal: what moves somebody up this list. */
const changesOffenders = (event: Parameters<typeof changesBans>[0]) => changesBans(event) || changesMembers(event)
import { Card, CardAction, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Ago } from '@/components/Freshness'
import { SwitchBank } from '@/components/ui/switch-bank'
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
        <EmptyRow tone="danger">{error}</EmptyRow>
      </Card>
    )
  }

  if (!list) {
    return (
      <Card>
        <EmptyRow>Loading…</EmptyRow>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <span className="font-mono">{list.total}</span> {list.total === 1 ? 'person' : 'people'} acted on more than once
        </CardTitle>
        <CardAction>
          <SwitchBank
            size="sm"
            value={status}
            onChange={setStatus}
            options={[
              { value: 'all', label: 'Everyone' },
              { value: 'repeat', label: 'Repeat only' },
            ]}
          />
        </CardAction>
      </CardHeader>

      <div
        className="border-b-(length:--hairline) px-(--panel-pad) py-2 text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <p>{list.rule}</p>
        <p className="mt-1">
          {list.lastRunAt ? <>Counts rebuilt <Ago iso={list.lastRunAt} now={list.now} />.</> : 'Counts not built yet.'}
        </p>
      </div>

      {list.people.length === 0 ? (
        <EmptyRow>Nobody yet</EmptyRow>
      ) : (
        <Table
          pinFirst
          head={
            <>
              <Th>Person</Th>
              <Th>Status</Th>
              <Th className="text-right">Actions</Th>
              <Th className="text-right">Last 30 days</Th>
              <Th className="text-right">Kicks</Th>
              <Th className="text-right">Warns</Th>
              <Th className="text-right">Bans</Th>
              <Th className="text-right">Removed</Th>
              <Th className="text-right">Turned away</Th>
              <Th className="text-right">Moderators</Th>
              <Th>Last action</Th>
            </>
          }
        >
          {list.people.map((p) => (
            <Tr key={`${p.who.platform}:${p.who.id}`} className="hover:bg-muted/40">
              <Td>
                <SubjectLink id={p.who.id} name={p.who.name} onOpen={onOpenSubject} />
              </Td>
              <Td>
                <StatusPill status={p.status} />
              </Td>
              <Num n={p.actions} strong />
              <Num n={p.actionsLast30Days} />
              <Num n={p.instanceKicks} />
              <Num n={p.warns} />
              <Num n={p.bans} />
              <Num n={p.removals} />
              <Num n={p.rejections} />
              <Num n={p.moderators} />
              <Td className="text-muted-foreground">
                <div>
                  {p.lastActionLabel}
                  {p.lastBy && (
                    <>
                      {' '}by <SubjectLink id={p.lastBy.id} name={p.lastBy.name} onOpen={onOpenSubject} />
                    </>
                  )}
                </div>
                <div className="font-mono text-muted-foreground" title={formatDay(p.lastActionAt)}>
                  {ago(p.lastActionAt, list.now)}
                </div>
              </Td>
            </Tr>
          ))}
        </Table>
      )}
    </Card>
  )
}

function Num({ n, strong }: { n: number; strong?: boolean }) {
  return (
    <Td className={cn('text-right font-mono', strong ? 'font-medium' : 'text-muted-foreground')}>
      {n === 0 ? '·' : n.toLocaleString()}
    </Td>
  )
}
