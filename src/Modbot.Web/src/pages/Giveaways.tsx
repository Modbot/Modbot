import { useCallback, useEffect, useMemo, useState } from 'react'
import { GiveawayForm } from '@/components/giveaways/GiveawayForm'
import { Pager } from '@/components/Pager'
import { usePageState } from '@/lib/listPage'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Row } from '@/components/ui/fact-row'
import { SwitchBank } from '@/components/ui/switch-bank'
import { SubjectLink } from '@/components/facts'
import { ApiError } from '@/lib/api'
import {
  ENTRY_WAY_LABEL,
  POST_STATE_LABEL,
  STATE_LABEL,
  WEIGHTING_LABEL,
  giveawayApi,
  measured,
  type Giveaway,
  type GiveawayDraw,
  type GiveawayEntrant,
  type GiveawayList,
  type GiveawayState,
} from '@/lib/giveaways'
import { useLocation } from '@/lib/router'
import { cn } from '@/lib/utils'
import { PageMessage } from '@/pages/analytics/shared'
import { dateTime } from '@/components/charts/format'
import { formatDay } from '@/lib/format'

type Filter = 'all' | GiveawayState

const FILTERS: { value: Filter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'open', label: 'Open' },
  { value: 'closed', label: 'Closed' },
  { value: 'drawn', label: 'Drawn' },
  { value: 'draft', label: 'Drafts' },
  { value: 'cancelled', label: 'Cancelled' },
]

/**
 * Giveaways (giveaways design §8): the list by status, one giveaway's rules and draws, the frozen
 * entrant list with every weight, and the seed — its promise before the draw and the seed itself
 * after.
 */
export function Giveaways() {
  // `?giveaway=` opens one: what the Details button on a Discord post links to. Read once, as the
  // page's opening state, so closing the dialog does not reopen it on the next render.
  const [location] = useLocation()

  const [filter, setFilter] = useState<Filter>('all')
  const [data, setData] = useState<GiveawayList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(() => location.search.get('giveaway'))
  const [editing, setEditing] = useState<Giveaway | 'new' | null>(null)

  const load = useCallback(() => {
    giveawayApi
      .list()
      .then((list) => {
        setData(list)
        setError(null)
      })
      .catch((e: unknown) => {
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the giveaways.',
        )
      })
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const shown = useMemo(
    () => (data?.giveaways ?? []).filter((g) => filter === 'all' || g.state === filter),
    [data, filter],
  )

  const opened = data?.giveaways.find((g) => g.id === openId) ?? null

  if (!data) return <PageMessage tone={error ? 'danger' : undefined}>{error ?? 'Loading…'}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <SwitchBank value={filter} onChange={setFilter} options={FILTERS} />
        <div className="flex-1" />
        {data.canRun && <Button onClick={() => setEditing('new')}>New giveaway</Button>}
      </div>

      {error && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}

      {shown.length === 0 ? (
        <PageMessage>No giveaways.</PageMessage>
      ) : (
        <Card className="divide-y-(--hairline) divide-border">
          {shown.map((giveaway) => (
            <GiveawayRow key={giveaway.id} giveaway={giveaway} onOpen={() => setOpenId(giveaway.id)} />
          ))}
        </Card>
      )}

      {opened && (
        <GiveawayDialog
          giveaway={opened}
          canRun={data.canRun}
          onClose={() => setOpenId(null)}
          onEdit={() => {
            setEditing(opened)
            setOpenId(null)
          }}
          onChanged={load}
        />
      )}

      {editing && (
        <GiveawayForm
          giveaway={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={(saved) => {
            setEditing(null)
            load()
            setOpenId(saved.id)
          }}
        />
      )}
    </div>
  )
}

function GiveawayRow({ giveaway, onOpen }: { giveaway: Giveaway; onOpen: () => void }) {
  const winners = giveaway.draws[0]?.winners ?? []

  return (
    <div className="flex min-h-(--row-h) flex-wrap items-center gap-x-3 gap-y-1 px-(--panel-pad) py-1.5">
      <span
        className="w-40 shrink-0 font-mono text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {formatDay(giveaway.closesAt)}
      </span>

      <button type="button" className="font-medium hover:underline" onClick={onOpen}>
        {giveaway.name}
      </button>

      <StateBadge state={giveaway.state} />

      {giveaway.entryWay === 'react' && (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span className="font-mono">{giveaway.entryCount}</span> entered
        </span>
      )}

      {winners.length > 0 && (
        <span style={{ fontSize: 'var(--text-small)' }}>
          {winners.map((w) => (
            <WinnerName key={w.position} entrant={w} />
          ))}
        </span>
      )}

      <div className="flex-1" />

      {giveaway.post && giveaway.post.state !== 'removed' && (
        <Badge variant={giveaway.post.state === 'failed' ? 'destructive' : 'secondary'}>
          Post: {POST_STATE_LABEL[giveaway.post.state]}
        </Badge>
      )}
    </div>
  )
}

function GiveawayDialog({
  giveaway,
  canRun,
  onClose,
  onEdit,
  onChanged,
}: {
  giveaway: Giveaway
  canRun: boolean
  onClose: () => void
  onEdit: () => void
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const act = (run: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    run()
      .then(() => onChanged())
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not do that.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={giveaway.name} className="max-w-[860px]" bodyClassName="max-h-[78vh] overflow-y-auto">
        <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
          <div className="flex flex-wrap items-center gap-2">
            <StateBadge state={giveaway.state} />
            <Badge variant="secondary">{ENTRY_WAY_LABEL[giveaway.entryWay]}</Badge>
            {giveaway.entryWay === 'react' && <Badge variant="secondary">{giveaway.emoji}</Badge>}
            <Badge variant="secondary">
              {giveaway.winnerCount === 1 ? '1 winner' : `${giveaway.winnerCount} winners`}
            </Badge>
            {giveaway.weighting !== 'uniform' && (
              <Badge variant="secondary">
                {WEIGHTING_LABEL[giveaway.weighting] ?? giveaway.weighting}
                {giveaway.weightCap !== null && ` · cap ${giveaway.weightCap}`}
              </Badge>
            )}
          </div>

          {giveaway.prize && <p className="whitespace-pre-wrap">{giveaway.prize}</p>}

          <div className="max-w-lg text-foreground">
            <Row label="Opens" value={dateTime(giveaway.opensAt)} mono />
            <Row label="Closes" value={dateTime(giveaway.closesAt)} mono />
            <Row
              label="Draw"
              value={giveaway.drawAt ? dateTime(giveaway.drawAt) : 'By hand'}
              mono={giveaway.drawAt !== null}
            />
            {giveaway.entryWay === 'react' && <Row label="Entries" value={giveaway.entryCount} mono />}
          </div>

          <Section title="Rules">
            <ul className="flex flex-col gap-0.5">
              {giveaway.ruleLines.map((line, i) => (
                <li key={i}>{line}</li>
              ))}
            </ul>
          </Section>

          {giveaway.exclusionLines.length > 0 && (
            <Section title="Not eligible">{giveaway.exclusionLines.join(', ')}</Section>
          )}

          <Section title="Seed">
            <code className="font-mono break-all text-muted-foreground">{giveaway.seedPromise}</code>
          </Section>

          {giveaway.post?.error && (
            <Section title="Discord">
              <span className="text-destructive">{giveaway.post.error}</span>
            </Section>
          )}

          {giveaway.draws.map((draw) => (
            <DrawPanel key={draw.id} giveaway={giveaway} draw={draw} />
          ))}

          {error && <p className="text-destructive">{error}</p>}

          {canRun && (
            <div className="flex flex-wrap justify-end gap-2">
              {giveaway.state !== 'cancelled' && giveaway.state !== 'drawn' && (
                <Button variant="outline" disabled={busy} onClick={onEdit}>
                  Edit
                </Button>
              )}
              {giveaway.state === 'closed' && (
                <Button variant="outline" disabled={busy} onClick={() => act(() => giveawayApi.open(giveaway.id))}>
                  Open
                </Button>
              )}
              {giveaway.state === 'open' && (
                <Button variant="outline" disabled={busy} onClick={() => act(() => giveawayApi.close(giveaway.id))}>
                  Close
                </Button>
              )}
              {(giveaway.state === 'closed' || giveaway.state === 'drawn' || giveaway.state === 'open') && (
                <Button disabled={busy} onClick={() => act(() => giveawayApi.draw(giveaway.id))}>
                  {giveaway.drawCount > 0 ? 'Draw again' : 'Draw'}
                </Button>
              )}
              {giveaway.state !== 'cancelled' && (
                <Button
                  variant="outline"
                  disabled={busy}
                  onClick={() => act(() => giveawayApi.cancel(giveaway.id))}
                >
                  Cancel giveaway
                </Button>
              )}
              <Button
                variant="destructive"
                disabled={busy}
                onClick={() => act(() => giveawayApi.remove(giveaway.id).then(onClose))}
              >
                Delete
              </Button>
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

/** One draw: its seed, its parameters, its winners, and the frozen entrant list a page at a time. */
function DrawPanel({ giveaway, draw }: { giveaway: Giveaway; draw: GiveawayDraw }) {
  const [entrants, setEntrants] = useState<GiveawayEntrant[] | null>(null)
  const at = usePageState()
  const [total, setTotal] = useState(0)
  const [open, setOpen] = useState(false)

  useEffect(() => {
    if (!open) return

    let cancelled = false
    giveawayApi
      .entrants(giveaway.id, draw.id, at.page)
      .then((answer) => {
        if (cancelled) return
        setEntrants(answer.people)
        setTotal(answer.total)
      })
      .catch(() => {
        if (!cancelled) setEntrants([])
      })

    return () => {
      cancelled = true
    }
  }, [open, at.page, giveaway.id, draw.id])

  return (
    <Section title={`Draw ${draw.number}`}>
      <div className="max-w-lg text-foreground">
        <Row
          label="Drawn"
          value={
            <>
              <span className="font-mono">{dateTime(draw.drawnAt)}</span>
              {draw.drawnBy && ` by ${draw.drawnBy}`}
            </>
          }
        />
        <Row
          label="Entrants"
          value={
            <>
              <span className="font-mono">{draw.inDrawCount}</span> of <span className="font-mono">{draw.entrantCount}</span>
              {draw.closeCalls > 0 && (
                <>
                  {' · '}
                  <span className="font-mono">{draw.closeCalls}</span> near the line
                </>
              )}
            </>
          }
        />
        <Row label="Total weight" value={draw.totalWeight} mono />
        <Row label="Promise" value={<code className="font-mono break-all">{draw.seedPromise}</code>} />
        <Row
          label="Seed"
          value={
            <>
              <code className="font-mono break-all">{draw.seed}</code>
              {!draw.seedKept && <span className="ml-2 text-destructive">Does not match the promise</span>}
            </>
          }
        />
      </div>

      <div className="flex flex-wrap gap-2">
        {draw.winners.map((w) => (
          <Badge key={w.position} variant="secondary">
            {w.winnerRank}. {w.purged ? '(erased)' : (w.name ?? w.key)} · {w.weight}
          </Badge>
        ))}
      </div>

      <div>
        <Button type="button" variant="outline" size="sm" onClick={() => setOpen((o) => !o)}>
          {open ? 'Hide entrants' : 'Entrants'}
        </Button>
      </div>

      {open && entrants && (
        <Card>
          <Table
            head={
              <>
                <Th>#</Th>
                <Th>Name</Th>
                <Th className="text-right">Weight</Th>
                <Th className="text-right">{WEIGHTING_LABEL[draw.weighting] ?? draw.weighting}</Th>
                <Th>In the draw</Th>
              </>
            }
          >
            {entrants.map((e) => (
              <Tr key={e.position}>
                <Td className="font-mono text-muted-foreground">{e.position + 1}</Td>
                <Td>
                  <EntrantName entrant={e} />
                </Td>
                <Td className="text-right font-mono">{e.weight}</Td>
                <Td
                  className={cn('text-right font-mono', e.closeCall && 'text-warn')}
                  title={e.closeCall ? 'Near the line' : undefined}
                >
                  {draw.weighting === 'uniform' ? '—' : measured(e.measured, e.fromPolledData)}
                </Td>
                <Td>
                  {e.winnerRank !== null ? (
                    <span className="text-ok">Won ({e.winnerRank})</span>
                  ) : e.keptOut === '' ? (
                    'Yes'
                  ) : (
                    <span className="text-muted-foreground">{e.because ?? e.keptOutLabel}</span>
                  )}
                </Td>
              </Tr>
            ))}
          </Table>
          <Pager at={at} pages={Math.max(1, Math.ceil(total / 50))} />
        </Card>
      )}
    </Section>
  )
}

function EntrantName({ entrant }: { entrant: GiveawayEntrant }) {
  if (entrant.purged) return <span className="text-muted-foreground">(erased)</span>

  const name = entrant.name ?? entrant.key

  return entrant.vrChatUserId ? (
    <SubjectLink id={entrant.vrChatUserId} name={name} />
  ) : (
    <span>{name}</span>
  )
}

function WinnerName({ entrant }: { entrant: GiveawayEntrant }) {
  return (
    <span className="mr-2">
      <EntrantName entrant={entrant} />
    </span>
  )
}

function StateBadge({ state }: { state: GiveawayState }) {
  return (
    <Badge
      variant={
        state === 'cancelled' ? 'destructive' : state === 'open' || state === 'drawn' ? 'default' : 'secondary'
      }
    >
      {STATE_LABEL[state]}
    </Badge>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-2 border-t border-t-(length:--hairline) pt-3">
      <span className="font-label">{title}</span>
      {children}
    </div>
  )
}
