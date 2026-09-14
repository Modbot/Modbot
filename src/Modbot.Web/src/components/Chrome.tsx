import { Button } from '@/components/ui/button'
import { GateIndicator } from '@/components/GateIndicator'
import type { CurrentUser } from '@/lib/api'
import { NAV, mayOpen, type NavItem, type PageId } from '@/lib/nav'
import { cn } from '@/lib/utils'
import type { Density, Theme } from '@/lib/preferences'
import { Headset, LogOut, Moon, Rows3, Rows2, Sun, UserRound } from 'lucide-react'

export function Sidebar({
  page,
  me,
  onNavigate,
  groupName,
  badges,
}: {
  page: PageId
  me: CurrentUser
  onNavigate: (p: PageId) => void
  groupName?: string
  /** A count to show beside an entry -- open reviews beside Reviews. Zero or absent shows nothing. */
  badges?: Partial<Record<PageId, number>>
}) {
  const visible = NAV.filter((item) => !('hidden' in item && item.hidden) && mayOpen(me, item.id))

  // A group heading travels with its first *visible* entry, so hiding "Users" does not take the
  // "Team" heading away from "Roles".
  const rows = visible.reduce<{ item: NavItem; showGroup: boolean; group?: string }[]>((acc, item) => {
    const group = 'group' in item ? item.group : undefined
    const previous = acc.length ? acc[acc.length - 1].group : undefined
    acc.push({ item, showGroup: group !== undefined && group !== previous, group: group ?? previous })
    return acc
  }, [])

  return (
    <aside className="flex flex-col gap-px border-r bg-card px-3 py-4" style={{ borderRightWidth: 'var(--hairline)' }}>
      <div className="flex items-center gap-2 px-2 pb-5">
        <div className="grid size-7 shrink-0 place-items-center rounded-md bg-primary text-sm font-semibold text-primary-foreground">M</div>
        <div className="leading-tight">
          <div className="font-semibold tracking-tight">Modbot</div>
          {groupName && (
            <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {groupName}
            </div>
          )}
        </div>
      </div>

      {rows.map(({ item, showGroup }) => (
        <div key={item.id}>
          {showGroup && 'group' in item && (
            <div className="px-2 pb-1 pt-4 text-[0.6875rem] font-semibold uppercase tracking-wider text-muted-foreground/70">
              {item.group}
            </div>
          )}
          <button
            onClick={() => onNavigate(item.id)}
            className={cn(
              'flex w-full items-center justify-between rounded-md px-2 font-medium transition-colors',
              page === item.id
                ? 'bg-accent text-accent-foreground'
                : 'text-muted-foreground hover:bg-secondary hover:text-foreground',
            )}
            style={{ height: 'var(--control-h)' }}
          >
            <span>{item.label}</span>
            {badges?.[item.id] ? (
              <span
                className="rounded-full bg-primary px-1.5 font-mono text-primary-foreground"
                style={{ fontSize: '0.6875rem', lineHeight: '1.25rem' }}
                aria-label={`${badges[item.id]} waiting`}
              >
                {badges[item.id]}
              </span>
            ) : null}
          </button>
        </div>
      ))}

      {/*
        The gate's health (spec 4.3.3). Real now: it reads /api/health/gate, reports the server's
        status rather than a colour picked here, and shows "unknown" rather than green when the
        fetch fails. The prototype's second line -- the live request-rate room left -- is still
        absent, because the effective rates are per bucket and there is no measured total to put
        against the 2 req/s ceiling without inventing one.
      */}
      <div className="mt-auto pt-4">
        <GateIndicator onOpen={() => onNavigate('health')} />
      </div>
    </aside>
  )
}

export function Topbar({
  title, density, setDensity, theme, setTheme, username, onAccount, onSignOut,
}: {
  title: string
  density: Density; setDensity: (d: Density) => void
  theme: Theme; setTheme: (t: Theme) => void
  username?: string
  onAccount?: () => void
  onSignOut?: () => void
}) {
  return (
    <header
      className="sticky top-0 z-10 flex items-center gap-3 border-b bg-background/85 px-5 py-3 backdrop-blur"
      style={{ borderBottomWidth: 'var(--hairline)' }}
    >
      <h1 className="font-semibold tracking-tight" style={{ fontSize: 'calc(var(--text-base) + 2px)' }}>{title}</h1>
      <div className="flex-1" />

      <Segmented
        label="Density"
        value={density}
        onChange={setDensity}
        options={[
          { value: 'dense', label: 'Dense', icon: <Rows3 className="size-3.5" /> },
          { value: 'comfortable', label: 'Comfy', icon: <Rows2 className="size-3.5" /> },
          { value: 'vr', label: 'VR', icon: <Headset className="size-3.5" /> },
        ]}
      />

      <Button variant="ghost" size="sm" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>
        {theme === 'dark' ? <Sun className="size-4" /> : <Moon className="size-4" />}
      </Button>

      {/* Your account: username, password, where a reset link reaches you, sign out everywhere. */}
      {onAccount && (
        <Button variant="ghost" size="sm" onClick={onAccount} title="Your account">
          <UserRound className="size-4" />
          {username && <span className="max-w-[10rem] truncate">{username}</span>}
        </Button>
      )}

      {/* Disabling an account or changing its roles now takes effect on the next request, so this
          is the ordinary way out rather than the emergency one -- but a moderator handing back a
          shared machine still needs it. */}
      {onSignOut && (
        <Button variant="ghost" size="sm" onClick={onSignOut} title="Sign out">
          <LogOut className="size-4" />
        </Button>
      )}
    </header>
  )
}

function Segmented<T extends string>({
  label, value, onChange, options,
}: {
  label: string; value: T; onChange: (v: T) => void
  options: { value: T; label: string; icon?: React.ReactNode }[]
}) {
  return (
    <div role="group" aria-label={label} className="flex gap-0.5 rounded-md border bg-secondary p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          onClick={() => onChange(o.value)}
          aria-pressed={value === o.value}
          className={cn(
            'flex items-center gap-1.5 rounded px-2 font-medium transition-colors',
            value === o.value ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
          )}
          style={{ fontSize: 'var(--text-small)', height: 'calc(var(--control-h) - 6px)' }}
        >
          {o.icon}
          {o.label}
        </button>
      ))}
    </div>
  )
}
