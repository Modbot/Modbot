import { Button } from '@/components/ui/button'
import { GateIndicator } from '@/components/GateIndicator'
import { cn } from '@/lib/utils'
import type { Density, Theme } from '@/lib/preferences'
import { Headset, LogOut, Moon, Rows3, Rows2, Sun } from 'lucide-react'

// No counts beside the labels yet. The prototype shows "14,208" next to Members, and it will
// again -- but a hardcoded number in a running deployment is indistinguishable from a real one,
// and a moderator has no way to tell they are looking at a screenshot. Counts return with the
// member sync that produces them (M1).
const NAV = [
  { id: 'members', label: 'Members' },
  { id: 'bans', label: 'Bans' },
  { id: 'audit', label: 'Audit log' },

  // One page per question (spec 10.1), not one "metrics" page. Tracked Groups is a later
  // feature (spec 10.3) and has no entry until it exists.
  { id: 'analytics-group', label: 'My Group', group: 'Analytics' },
  { id: 'analytics-team', label: 'My Team' },
  { id: 'analytics-worlds', label: 'Worlds' },
  { id: 'health', label: 'Sync health', group: 'Setup' },
  { id: 'settings', label: 'Settings' },
] as const

export type PageId = (typeof NAV)[number]['id']

export function Sidebar({
  page,
  onNavigate,
  groupName,
}: {
  page: PageId
  onNavigate: (p: PageId) => void
  groupName?: string
}) {
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

      {NAV.map((item) => (
        <div key={item.id}>
          {'group' in item && item.group && (
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
  title, subtitle, density, setDensity, theme, setTheme, onSignOut,
}: {
  title: string; subtitle?: string
  density: Density; setDensity: (d: Density) => void
  theme: Theme; setTheme: (t: Theme) => void
  onSignOut?: () => void
}) {
  return (
    <header
      className="sticky top-0 z-10 flex items-center gap-3 border-b bg-background/85 px-5 py-3 backdrop-blur"
      style={{ borderBottomWidth: 'var(--hairline)' }}
    >
      <div>
        <h1 className="font-semibold tracking-tight" style={{ fontSize: 'calc(var(--text-base) + 2px)' }}>{title}</h1>
        {subtitle && <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>{subtitle}</div>}
      </div>
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

      {/* Sessions last 14 days and a permission change only takes effect at the next sign-in
          (docs/security.md), so a moderator handing back a shared machine -- or one who was just
          granted something -- needs a way out that is not "clear your cookies". */}
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
