import { useCallback, useEffect, useRef, useState } from 'react'
import { Headset, Moon, Rows2, Rows3, Sun } from 'lucide-react'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { Popups } from './Popups'
import { GROUP_NAME, clock, initialLive, personById, step, worldById, type InstanceState } from './simulation'
import { closeOne, open, type Stack, type Subject } from './stack'

type Density = 'dense' | 'comfortable' | 'vr'

const NAV: { label: string; group?: string }[] = [
  { label: 'Members' },
  { label: 'Live' },
  { label: 'Chat' },
  { label: 'Bans' },
  { label: 'Flags' },
  { label: 'Audit log' },
  { label: 'My Group', group: 'Analytics' },
  { label: 'My Team' },
  { label: 'Worlds' },
  { label: 'Instances' },
  { label: 'Reviews', group: 'Team' },
  { label: 'Users' },
  { label: 'Roles' },
  { label: 'Sync health', group: 'Setup' },
  { label: 'Settings' },
]

/** How often the sample evening moves on. */
const TICK_MS = 2600

/**
 * A working copy of Modbot's Live page, with its sidebar, its density switch and its popups, playing
 * through a made-up evening. Built from the app's own layout and tokens rather than a screenshot, so
 * it is sharp at any size, follows the page's theme, and can be clicked.
 */
export function AppMock({
  label,
  startWith = [],
  play = true,
  className,
}: {
  /** Names the copy for assistive technology. */
  label: string
  startWith?: Stack
  /** False holds the evening still. */
  play?: boolean
  className?: string
}) {
  const [live, setLive] = useState(initialLive)
  const [stack, setStack] = useState<Stack>(startWith)
  const [density, setDensity] = useState<Density>('dense')
  const [theme, toggleTheme] = useTheme()
  const root = useRef<HTMLElement>(null)
  const openers = useRef<(HTMLElement | null)[]>([])

  // Moves on only while the copy is on screen and the tab is visible: nothing runs for a visitor
  // who has scrolled past it.
  useEffect(() => {
    if (!play || !root.current) return

    let visible = false
    let timer: number | undefined

    const follow = () => {
      window.clearInterval(timer)
      timer = undefined
      if (visible && document.visibilityState === 'visible') {
        timer = window.setInterval(() => setLive((s) => step(s)), TICK_MS)
      }
    }

    const observer = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting
      follow()
    })
    observer.observe(root.current)
    document.addEventListener('visibilitychange', follow)

    return () => {
      observer.disconnect()
      document.removeEventListener('visibilitychange', follow)
      window.clearInterval(timer)
    }
  }, [play])

  const openSubject = useCallback((subject: Subject) => {
    openers.current.push(document.activeElement as HTMLElement | null)
    setStack((s) => open(s, subject))
  }, [])

  const closeSubject = useCallback(() => {
    setStack((s) => closeOne(s))
    const opener = openers.current.pop()
    // Back where the moderator was: focus returns to whatever opened the popup.
    if (opener && root.current?.contains(opener)) window.setTimeout(() => opener.focus({ preventScroll: true }), 0)
  }, [])

  return (
    // Escape closes one popup, like the app. The handler sits on the frame because the key comes from
    // whichever control inside it has focus.
    // oxlint-disable-next-line jsx-a11y/no-noninteractive-element-interactions
    <section
      ref={root}
      aria-label={label}
      data-density={density}
      onKeyDown={(e) => {
        if (e.key === 'Escape' && stack.length > 0) {
          e.stopPropagation()
          closeSubject()
        }
      }}
      className={cn(
        'app relative flex flex-col overflow-hidden rounded-xl border bg-background text-foreground shadow-[0_1px_0_0_var(--border),0_30px_80px_-30px_rgb(22_24_31/0.35)] dark:shadow-[0_30px_80px_-30px_rgb(0_0_0/0.8)]',
        className,
      )}
    >
      <div className="flex h-9 shrink-0 items-center gap-3 border-b bg-card px-3" style={{ borderBottomWidth: 'var(--hairline)' }}>
        <div className="flex gap-1.5" aria-hidden="true">
          <span className="size-2.5 rounded-full bg-border" />
          <span className="size-2.5 rounded-full bg-border" />
          <span className="size-2.5 rounded-full bg-border" />
        </div>
        <div className="min-w-0 flex-1 truncate rounded-md bg-secondary px-2.5 py-0.5 text-center font-mono text-[11px] text-muted-foreground">
          modbot.your-group.example/live
        </div>
        <span className="hidden shrink-0 text-[11px] text-muted-foreground sm:inline">Sample data</span>
      </div>

      <div className="relative grid min-h-0 flex-1 md:grid-cols-[12.5rem_minmax(0,1fr)]">
        <div
          className="hidden flex-col gap-px overflow-hidden border-r bg-card px-3 py-4 md:flex"
          style={{ borderRightWidth: 'var(--hairline)' }}
        >
          <div className="flex items-center gap-2 px-2 pb-4">
            <img src="/icon-512.png" alt="" width={28} height={28} className="size-7 shrink-0" />
            <div className="min-w-0 leading-tight">
              <div className="font-semibold tracking-tight">Modbot</div>
              <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>{GROUP_NAME}</div>
            </div>
          </div>
          {NAV.map((item) => (
            <div key={item.label}>
              {item.group && (
                <div className="px-2 pt-3 pb-1 text-[0.6875rem] font-semibold text-muted-foreground">{item.group}</div>
              )}
              <div
                aria-current={item.label === 'Live' ? 'page' : undefined}
                className={cn(
                  'flex items-center rounded-md px-2 font-medium',
                  item.label === 'Live' ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
                )}
                style={{ height: 'var(--control-h)' }}
              >
                {item.label}
              </div>
            </div>
          ))}
          <div className="mt-auto flex items-center gap-2 px-2 pt-4" style={{ fontSize: 'var(--text-small)' }}>
            <span className="size-1.5 shrink-0 rounded-full bg-ok" />
            <span className="truncate text-muted-foreground">VRChat · working</span>
          </div>
        </div>

        <div className="flex min-h-0 min-w-0 flex-col">
          <header
            className="flex shrink-0 items-center gap-2 border-b bg-background/85 px-3 py-2.5 sm:gap-3 sm:px-5"
            style={{ borderBottomWidth: 'var(--hairline)' }}
          >
            <div className="font-semibold tracking-tight" style={{ fontSize: 'calc(var(--text-base) + 2px)' }}>
              Live
            </div>
            <span className="live-dot size-1.5 rounded-full bg-ok" aria-hidden="true" />
            <div className="flex-1" />
            <div role="group" aria-label="Density" className="flex gap-0.5 rounded-md border bg-secondary p-0.5">
              {(
                [
                  { value: 'dense', label: 'Dense', icon: Rows3 },
                  { value: 'comfortable', label: 'Comfy', icon: Rows2 },
                  { value: 'vr', label: 'VR', icon: Headset },
                ] as const
              ).map((o) => (
                <button
                  key={o.value}
                  type="button"
                  onClick={() => setDensity(o.value)}
                  aria-pressed={density === o.value}
                  className={cn(
                    'flex items-center gap-1.5 rounded px-2 font-medium transition-colors',
                    density === o.value ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
                  )}
                  style={{ fontSize: 'var(--text-small)', height: 'calc(var(--control-h) - 6px)' }}
                >
                  <o.icon className="size-3.5" aria-hidden="true" />
                  <span className={o.value === 'vr' ? undefined : 'hidden sm:inline'}>{o.label}</span>
                  {o.value !== 'vr' && <span className="sr-only sm:hidden">{o.label}</span>}
                </button>
              ))}
            </div>
            <button
              type="button"
              onClick={toggleTheme}
              aria-label={theme === 'dark' ? 'Switch to light' : 'Switch to dark'}
              className="grid size-7 place-items-center rounded-md text-muted-foreground hover:bg-secondary hover:text-foreground"
            >
              <Sun className="hidden size-4 dark:block" aria-hidden="true" />
              <Moon className="size-4 dark:hidden" aria-hidden="true" />
            </button>
          </header>

          <div className="min-h-0 flex-1 overflow-auto p-3 sm:p-5">
            <div className="grid gap-4 xl:grid-cols-2">
              {live.instances.map((instance) => (
                <InstanceCard key={instance.id} instance={instance} lastArrival={live.lastArrival} onOpen={openSubject} />
              ))}
            </div>
          </div>
        </div>

        <Popups stack={stack} live={live} onOpen={openSubject} onClose={closeSubject} />
      </div>
    </section>
  )
}

function Link({ children, onClick, className }: { children: React.ReactNode; onClick: () => void; className?: string }) {
  return (
    <button type="button" onClick={onClick} className={cn('inline rounded text-left hover:underline', className)}>
      {children}
    </button>
  )
}

function InstanceCard({ instance, lastArrival, onOpen }: { instance: InstanceState; lastArrival: string | null; onOpen: (s: Subject) => void }) {
  const world = worldById(instance.worldId)
  const watched = instance.watching.length > 0
  const [a, b] = world.picture

  return (
    <div className="flex flex-col gap-3 rounded-xl border bg-card p-4 text-card-foreground" style={{ borderWidth: 'var(--hairline)' }}>
      <div className="flex items-start gap-3">
        <button
          type="button"
          onClick={() => onOpen({ kind: 'world', id: world.id })}
          aria-label={`Open ${world.name}`}
          className="aspect-[4/3] w-20 shrink-0 rounded-md sm:w-24"
          style={{ background: `radial-gradient(120% 90% at 20% 15%, ${a}, transparent 60%), linear-gradient(160deg, ${b}, #16181f)` }}
        />
        <div className="min-w-0 flex-1">
          <div className="truncate font-medium">
            <Link onClick={() => onOpen({ kind: 'world', id: world.id })} className="font-medium">
              {world.name}
            </Link>
          </div>
          <div className="flex flex-wrap items-center gap-x-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <Link
              onClick={() => onOpen({ kind: 'instance', id: instance.id })}
              className="inline-flex min-h-6 min-w-6 items-center font-mono font-medium text-foreground"
            >
              #{instance.instance}
            </Link>
            <span>{instance.access} · {instance.region.toUpperCase()}</span>
          </div>
        </div>
        <div className="shrink-0 text-right">
          <div className="font-mono text-2xl font-medium tabular-nums">{instance.headCount}</div>
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {instance.headCount === 1 ? 'person' : 'people'}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-baseline gap-x-2" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Watching</span>
        {watched ? (
          instance.watching.map((id) => (
            <Link key={id} onClick={() => onOpen({ kind: 'person', id })} className="font-medium">
              {personById(id).name}
            </Link>
          ))
        ) : (
          <span>Nobody watching</span>
        )}
      </div>

      {watched ? (
        <section className="flex flex-col gap-1">
          <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
            Here now · {instance.people.length}
          </div>
          <ul style={{ fontSize: 'var(--text-small)' }}>
            {instance.people.map((p) => {
              const who = personById(p.personId)
              return (
                <li
                  key={p.personId}
                  className={cn('-mx-1 flex flex-wrap items-baseline gap-x-2 border-t px-1 py-1', lastArrival === p.personId && 'arrived')}
                  style={{ borderTopWidth: 'var(--hairline)' }}
                >
                  <Link onClick={() => onOpen({ kind: 'person', id: who.id })} className="font-medium">
                    {who.name}
                  </Link>
                  {who.standing !== 'Ordinary' && (
                    <span
                      className={cn(
                        'rounded px-1.5',
                        who.standing === 'Flagged' ? 'bg-destructive/10 font-medium text-destructive' : 'bg-secondary text-muted-foreground',
                      )}
                      style={{ fontSize: '11px' }}
                    >
                      {who.standing}
                    </span>
                  )}
                  {who.flag && <span className="text-destructive">{who.flag}</span>}
                  <span className="ml-auto whitespace-nowrap text-muted-foreground tabular-nums">arrived {clock(p.arrivedAt)}</span>
                </li>
              )
            })}
          </ul>
        </section>
      ) : (
        <section className="flex flex-col gap-1 text-muted-foreground">
          <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
            Last seen {clock(instance.openedAt + 20)} · 2
          </div>
          <ul style={{ fontSize: 'var(--text-small)' }}>
            {['usr_haze', 'usr_lumen'].map((id) => (
              <li key={id} className="flex items-baseline gap-2 border-t py-1" style={{ borderTopWidth: 'var(--hairline)' }}>
                <Link onClick={() => onOpen({ kind: 'person', id })}>{personById(id).name}</Link>
                <span className="ml-auto tabular-nums">here before {clock(instance.openedAt + 20)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  )
}
