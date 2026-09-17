import { useEffect, useRef, useState } from 'react'
import { ArrowLeft, X } from 'lucide-react'
import { SourceBadge } from '@/components/SourceBadge'
import { cn } from '@/lib/utils'
import { instanceFacts, sheetFor, type Part, type SampleFact } from './sample'
import { clock, duration, personById, worldById, type LiveState } from './simulation'
import { backLabel, top, type Stack, type Subject } from './stack'

/**
 * The person, world and instance popups, drawn over the copy of the app the way the app draws them
 * over its pages: identity on the left, tabs on the right, stacking as one opens another.
 */
export function Popups({
  stack,
  live,
  onOpen,
  onClose,
}: {
  stack: Stack
  live: LiveState
  onOpen: (subject: Subject) => void
  onClose: () => void
}) {
  const subject = top(stack)
  const back = backLabel(stack)
  const closeRef = useRef<HTMLButtonElement>(null)
  const mounted = useRef(false)

  // Focus moves into a popup someone opened, never into one the page starts with: that would take
  // focus away from the top of the page on load.
  useEffect(() => {
    if (mounted.current && subject) closeRef.current?.focus({ preventScroll: true })
    mounted.current = true
  }, [subject, stack.length])

  if (!subject) return null

  const key = `${stack.length}:${subject.kind}:${subject.id}`

  return (
    <div className="absolute inset-0 z-20">
      <button
        type="button"
        tabIndex={-1}
        aria-hidden="true"
        className="absolute inset-0 cursor-default bg-foreground/30 backdrop-blur-[2px]"
        onClick={onClose}
      />
      <div
        key={key}
        role="dialog"
        aria-labelledby={`${key}-title`}
        className="popup-in absolute inset-2 flex flex-col overflow-hidden rounded-xl border bg-card text-card-foreground shadow-2xl sm:inset-5 lg:inset-x-10 lg:inset-y-6"
      >
        <div className="flex shrink-0 items-center gap-3 border-b px-4 py-2.5" style={{ borderBottomWidth: 'var(--hairline)' }}>
          {back && (
            <button
              type="button"
              onClick={onClose}
              aria-label={back}
              className="flex shrink-0 items-center gap-1 rounded-md px-2 py-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
              style={{ fontSize: 'var(--text-small)' }}
            >
              <ArrowLeft className="size-4" aria-hidden="true" />
              <span className="hidden sm:inline">{back}</span>
            </button>
          )}
          <div className="min-w-0 flex-1">
            <div id={`${key}-title`} className="truncate font-semibold tracking-tight">
              {title(subject, live)}
            </div>
            <div className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {subtitle(subject, live)}
            </div>
          </div>
          <button
            ref={closeRef}
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="shrink-0 rounded-md p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
          >
            <X className="size-4" aria-hidden="true" />
          </button>
        </div>

        <div className="grid min-h-0 flex-1 overflow-auto md:grid-cols-[15rem_minmax(0,1fr)] md:overflow-hidden">
          {subject.kind === 'person' && <PersonBody id={subject.id} live={live} onOpen={onOpen} />}
          {subject.kind === 'world' && <WorldBody id={subject.id} live={live} onOpen={onOpen} />}
          {subject.kind === 'instance' && <InstanceBody id={subject.id} live={live} onOpen={onOpen} />}
        </div>
      </div>
    </div>
  )
}

function title(subject: Subject, live: LiveState): string {
  if (subject.kind === 'person') return 'Person'
  if (subject.kind === 'world') return worldById(subject.id).name
  const instance = live.instances.find((r) => r.id === subject.id)
  return instance ? `${worldById(instance.worldId).name} #${instance.instance}` : 'Instance'
}

function subtitle(subject: Subject, live: LiveState): string {
  if (subject.kind === 'instance') return live.instances.some((r) => r.id === subject.id) ? 'Open now' : 'Closed'
  return subject.id
}

/* ── Pieces the three share ─────────────────────────────────────────────── */

function Left({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="flex flex-col gap-3 border-b p-4 md:overflow-auto md:border-r md:border-b-0"
      style={{ borderWidth: 0, borderRightWidth: 'var(--hairline)', borderBottomWidth: 'var(--hairline)' }}
    >
      {children}
    </div>
  )
}

function Tabs<T extends string>({
  tabs,
  children,
}: {
  tabs: { value: T; label: string; badge?: number }[]
  children: (value: T) => React.ReactNode
}) {
  const [value, setValue] = useState<T>(tabs[0].value)

  return (
    <div className="flex min-h-0 flex-col">
      <div role="tablist" className="flex shrink-0 items-center gap-1 border-b px-1" style={{ borderBottomWidth: 'var(--hairline)' }}>
        {tabs.map((tab) => (
          <button
            key={tab.value}
            type="button"
            role="tab"
            aria-selected={value === tab.value}
            onClick={() => setValue(tab.value)}
            className={cn(
              'relative rounded-t px-3 py-2 font-medium transition-colors',
              value === tab.value ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
            )}
            style={{ fontSize: 'var(--text-small)' }}
          >
            {tab.label}
            {tab.badge ? <span className="ml-1.5 text-muted-foreground tabular-nums">{tab.badge}</span> : null}
            {value === tab.value && <span className="absolute inset-x-1 -bottom-px h-0.5 rounded-full bg-foreground" />}
          </button>
        ))}
      </div>
      <div role="tabpanel" className="min-h-0 flex-1 overflow-auto p-4">
        {children(value)}
      </div>
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <span className="break-words">{children}</span>
    </div>
  )
}

function Figure({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border px-3 py-2" style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}>
      <div className="text-muted-foreground">{label}</div>
      <div className="mt-0.5 font-mono text-lg font-medium tracking-tight tabular-nums">{value}</div>
    </div>
  )
}

function Picture({ worldId, className }: { worldId: string; className?: string }) {
  const [a, b] = worldById(worldId).picture
  return (
    <div
      aria-hidden="true"
      className={cn('aspect-[4/3] w-full rounded-md', className)}
      style={{ background: `radial-gradient(120% 90% at 20% 15%, ${a}, transparent 60%), linear-gradient(160deg, ${b}, #16181f)` }}
    />
  )
}

export function NameLink({ part, onOpen, className }: { part: Exclude<Part, string>; onOpen: (s: Subject) => void; className?: string }) {
  return (
    <button
      type="button"
      onClick={() => onOpen({ kind: part.kind, id: part.id })}
      className={cn('inline rounded text-left font-medium hover:underline', className)}
    >
      {part.label}
    </button>
  )
}

function Facts({ facts, onOpen }: { facts: SampleFact[]; onOpen: (s: Subject) => void }) {
  return (
    <ol className="flex flex-col gap-2">
      {facts.map((fact) => (
        <li key={fact.id} className="rounded-md border px-3 py-2" style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}>
          <div className="flex items-center gap-2">
            <SourceBadge source={fact.source} />
            <span className="flex-1" />
            <span className="text-muted-foreground tabular-nums">{fact.time}</span>
          </div>
          <div className="mt-1">
            {fact.parts.map((part, i) =>
              typeof part === 'string' ? <span key={i}>{part}</span> : <NameLink key={i} part={part} onOpen={onOpen} />,
            )}
          </div>
        </li>
      ))}
    </ol>
  )
}

/* ── Person ─────────────────────────────────────────────────────────────── */

function PersonBody({ id, live, onOpen }: { id: string; live: LiveState; onOpen: (s: Subject) => void }) {
  const who = personById(id)
  const sheet = sheetFor(id, who.name)
  const here = live.instances.find((r) => r.people.some((p) => p.personId === id))

  return (
    <>
      <Left>
        <div className="flex items-center gap-3">
          <div
            aria-hidden="true"
            className="grid size-12 shrink-0 place-items-center rounded-full bg-accent text-lg font-semibold text-accent-foreground"
          >
            {who.name.slice(0, 1).toUpperCase()}
          </div>
          <div className="min-w-0">
            <div className="truncate font-semibold">{who.name}</div>
            <div className="flex items-center gap-1.5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <span className={cn('size-2 rounded-full', sheet.status === 'Active' ? 'bg-ok' : 'bg-warn')} />
              {sheet.status}
            </div>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-2">
          <Field label="Joined VRChat">{sheet.joinedVRChat}</Field>
          <Field label="Last platform">{sheet.platform}</Field>
        </div>
        <div className="rounded-md border px-3 py-2" style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}>
          <div className="font-medium">Membership</div>
          <p className="mt-1">{sheet.memberSince ? `Member since ${sheet.memberSince}.` : 'Not a member.'}</p>
          {sheet.roles.length > 0 && (
            <div className="mt-1 flex flex-wrap items-center gap-1">
              <span className="text-muted-foreground">Roles:</span>
              {sheet.roles.map((role) => (
                <span key={role} className="rounded-full bg-secondary px-2 py-0.5 font-medium">
                  {role}
                </span>
              ))}
            </div>
          )}
          <p className="mt-1 text-muted-foreground">Member list synced 4 minutes ago; ban list synced 4 minutes ago.</p>
        </div>
      </Left>

      <Tabs
        tabs={[
          { value: 'logs', label: 'Logs' },
          { value: 'cases', label: 'Cases' },
          { value: 'metrics', label: 'Metrics' },
        ]}
      >
        {(tab) => (
          <>
            {tab === 'logs' && (
              <div className="flex flex-col gap-3">
                <div className="grid grid-cols-3 gap-2">
                  <Figure label="Warnings" value={String(sheet.counts.warnings)} />
                  <Figure label="Kicks" value={String(sheet.counts.kicks)} />
                  <Figure label="Bans" value={String(sheet.counts.bans)} />
                </div>
                <div className="font-medium">Everything recorded about this person</div>
                <Facts facts={sheet.facts} onOpen={onOpen} />
              </div>
            )}
            {tab === 'cases' && <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>No case files.</p>}
            {tab === 'metrics' && (
              <div className="flex flex-col gap-3">
                <div className="font-medium">Time in world</div>
                <div className="grid grid-cols-2 gap-2 xl:grid-cols-3">
                  <Figure label="Time seen" value={sheet.metrics.timeSeen} />
                  <Figure label="Instances visited" value={String(sheet.metrics.instances)} />
                  <Figure label="Worlds visited" value={String(sheet.metrics.worlds)} />
                  <Figure label="Arrivals" value={String(sheet.metrics.arrivals)} />
                  <Figure label="Last seen" value={here ? 'now' : '2 days ago'} />
                  <Figure label="First seen" value={sheet.metrics.firstSeen} />
                </div>
              </div>
            )}
          </>
        )}
      </Tabs>
    </>
  )
}

/* ── World ──────────────────────────────────────────────────────────────── */

const VISITORS = [18, 22, 15, 27, 31, 44, 39, 21, 25, 19, 30, 36, 48, 41]

function WorldBody({ id, live, onOpen }: { id: string; live: LiveState; onOpen: (s: Subject) => void }) {
  const world = worldById(id)
  const instances = live.instances.filter((r) => r.worldId === id)
  const max = Math.max(...VISITORS)

  return (
    <>
      <Left>
        <Picture worldId={id} />
        <Field label="Made by">{world.author}</Field>
        <Field label="Holds">{world.capacity} people</Field>
        <Field label="Who can find it">Anyone</Field>
        <Field label="First seen by Modbot">14 Feb 2026</Field>
      </Left>

      <Tabs
        tabs={[
          { value: 'instances', label: 'Instances', badge: 38 + instances.length },
          { value: 'metrics', label: 'Metrics' },
        ]}
      >
        {(tab) =>
          tab === 'instances' ? (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-left text-muted-foreground">
                  <tr>
                    <th className="py-1 pr-3 font-medium">Instance</th>
                    <th className="py-1 pr-3 text-right font-medium">People</th>
                    <th className="py-1 pr-3 text-right font-medium">Most at once</th>
                    <th className="py-1 pr-3 text-right font-medium">Open for</th>
                    <th className="py-1 font-medium">Started</th>
                  </tr>
                </thead>
                <tbody>
                  {instances.map((r) => (
                    <tr key={r.id} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                      <td className="py-1 pr-3">
                        <NameLink part={{ kind: 'instance', id: r.id, label: r.instance }} onOpen={onOpen} className="font-mono" />
                        <div className="text-muted-foreground">{r.access} · {r.region.toUpperCase()}</div>
                      </td>
                      <td className="py-1 pr-3 text-right tabular-nums">{r.headCount}</td>
                      <td className="py-1 pr-3 text-right tabular-nums">{r.peak}</td>
                      <td className="py-1 pr-3 text-right tabular-nums">{duration(live.minute - r.openedAt)}</td>
                      <td className="py-1 text-muted-foreground">
                        <div>Today {clock(r.openedAt)}</div>
                        <div>open now</div>
                      </td>
                    </tr>
                  ))}
                  {[
                    ['90412', 0, 14, '3h 02m', 'Sat 20:10', 'went quiet'],
                    ['55107', 0, 9, '1h 25m', 'Fri 22:37', 'went quiet'],
                  ].map(([number, now, peak, open, started, end]) => (
                    <tr key={String(number)} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                      <td className="py-1 pr-3 font-mono font-medium">{number}</td>
                      <td className="py-1 pr-3 text-right tabular-nums">{now ? now : '—'}</td>
                      <td className="py-1 pr-3 text-right tabular-nums">{peak}</td>
                      <td className="py-1 pr-3 text-right tabular-nums">{open}</td>
                      <td className="py-1 text-muted-foreground">
                        <div>{started}</div>
                        <div>{end}</div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="flex flex-col gap-3">
              <div className="font-medium">How busy this world has been</div>
              <div className="grid grid-cols-2 gap-2">
                <Figure label="Time seen" value="212h" />
                <Figure label="Visitors" value="186" />
              </div>
              <div style={{ fontSize: 'var(--text-small)' }} className="text-muted-foreground">
                Visitors per day
              </div>
              <div className="flex h-24 items-end gap-1" aria-hidden="true">
                {VISITORS.map((v, i) => (
                  <div key={i} className="flex-1 rounded-t-sm" style={{ height: `${(v / max) * 100}%`, background: 'var(--series-1)' }} />
                ))}
              </div>
            </div>
          )
        }
      </Tabs>
    </>
  )
}

/* ── Instance ───────────────────────────────────────────────────────────── */

function InstanceBody({ id, live, onOpen }: { id: string; live: LiveState; onOpen: (s: Subject) => void }) {
  const instance = live.instances.find((r) => r.id === id) ?? live.instances[0]
  const world = worldById(instance.worldId)
  const watched = instance.watching.length > 0

  return (
    <>
      <Left>
        <Picture worldId={instance.worldId} />
        <Field label="World">
          <NameLink part={{ kind: 'world', id: world.id, label: world.name }} onOpen={onOpen} />
        </Field>
        <Field label="Instance">
          <span className="font-mono">{instance.instance}</span>
        </Field>
        <Field label="Who can join">{instance.access}</Field>
        <Field label="Opened">Today {clock(instance.openedAt)}, open for {duration(live.minute - instance.openedAt)}</Field>
      </Left>

      <Tabs
        tabs={[
          { value: 'people', label: 'People', badge: watched ? instance.people.length : undefined },
          { value: 'logs', label: 'Logs', badge: instanceFacts(instance.id).length },
        ]}
      >
        {(tab) =>
          tab === 'people' ? (
            <div className="flex flex-col gap-2">
              <div className="font-medium">Who was seen in this instance</div>
              {watched ? (
                <ul style={{ fontSize: 'var(--text-small)' }}>
                  {instance.people.map((p) => {
                    const who = personById(p.personId)
                    return (
                      <li key={p.personId} className="flex items-baseline gap-2 border-t py-1" style={{ borderTopWidth: 'var(--hairline)' }}>
                        <NameLink part={{ kind: 'person', id: who.id, label: who.name }} onOpen={onOpen} />
                        <span className="ml-auto text-muted-foreground tabular-nums">arrived {clock(p.arrivedAt)}</span>
                      </li>
                    )
                  })}
                </ul>
              ) : (
                <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Nobody watching.
                </p>
              )}
            </div>
          ) : (
            <div className="flex flex-col gap-2">
              <div className="font-medium">What happened in this instance</div>
              <Facts facts={instanceFacts(instance.id)} onOpen={onOpen} />
            </div>
          )
        }
      </Tabs>
    </>
  )
}
