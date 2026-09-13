import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { Density, Theme } from '@/lib/preferences'
import { Headset, Moon, Rows3, Rows2, Sun } from 'lucide-react'

// No counts beside the labels yet. The prototype shows "14,208" next to Members, and it will
// again -- but a hardcoded number in a running deployment is indistinguishable from a real one,
// and a moderator has no way to tell they are looking at a screenshot. Counts return with the
// member sync that produces them (M1).
const NAV = [
  { id: 'members', label: 'Members' },
  { id: 'bans', label: 'Bans' },
  { id: 'audit', label: 'Audit log' },
  { id: 'metrics', label: 'Metrics', group: 'Insight' },
  { id: 'settings', label: 'Settings', group: 'Setup' },
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
        The gate's health and its rate-limit headroom belong here (spec 4.3.3) and the prototype
        shows both. They are left out rather than faked: a green "VRChat · healthy" dot that is
        really a hardcoded string is worse than no dot at all, because it is the one place an
        operator would look to find out that something is wrong.
      */}
      <div className="mt-auto" />
    </aside>
  )
}

export function Topbar({
  title, subtitle, density, setDensity, theme, setTheme,
}: {
  title: string; subtitle?: string
  density: Density; setDensity: (d: Density) => void
  theme: Theme; setTheme: (t: Theme) => void
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
