import { Button } from '@/components/ui/button'
import { DemoMarker } from '@/components/DemoMarker'
import { StatusRows } from '@/components/StatusRows'
import type { CurrentUser } from '@/lib/api'
import { CREDITS_PATH, NAV, mayOpen, type NavItem, type PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import type { StatusRowId } from '@/lib/status'
import { cn } from '@/lib/utils'
import { DOCS_URL } from '@/lib/docs'
import type { Density, Theme } from '@/lib/preferences'
import { followLink } from '@/lib/router'
import { Headset, LogOut, Moon, Rows3, Rows2, Sun, UserRound } from 'lucide-react'

/** The group this Modbot manages, as the status endpoint reports it. */
export type SidebarGroup = { name: string; iconUrl: string | null; bannerUrl: string | null }

export function Sidebar({
  page,
  me,
  onNavigate,
  onOpenHealth,
  group,
  badges,
}: {
  page: PageId
  me: CurrentUser
  onNavigate: (p: PageId) => void
  /** Opens the Health page at one part's card. */
  onOpenHealth: (section: StatusRowId) => void
  group?: SidebarGroup | null
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
      {/*
        The group at the top: its banner when VRChat has one, its icon and its name. This is the
        community's Modbot, and the sidebar says whose. Modbot's own mark moves to the foot.
      */}
      {group ? <GroupHeading group={group} /> : <ModbotHeading />}

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
        Modbot's own parts, one row each (spec 4.2.3, 4.3.3). Only for somebody who may read the
        operational log, which is the same line the Health page itself draws -- every row leads
        there, and rows that open a page this person cannot have would be a dead end.
      */}
      {can(me, 'ViewOperationalLog') && <StatusRows onOpen={onOpenHealth} />}

      {group && (
        <div className="mt-auto flex items-center gap-2 px-2 pt-4">
          <img src="/icon-512.png" alt="" width={20} height={20} className="size-5 shrink-0" />
          <span className="font-display text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Modbot
          </span>
        </div>
      )}
    </aside>
  )
}

function ModbotHeading() {
  return (
    <div className="flex items-center gap-2 px-2 pb-5">
      <img src="/icon-512.png" alt="" width={28} height={28} className="size-7 shrink-0" />
      <div className="font-display text-[0.9375rem] leading-none">Modbot</div>
    </div>
  )
}

function GroupHeading({ group }: { group: SidebarGroup }) {
  return (
    <div className="pb-5">
      {group.bannerUrl && (
        <img src={group.bannerUrl} alt="" className="mb-3 aspect-[3/1] w-full rounded-md object-cover" />
      )}
      <div className="flex items-center gap-2 px-2">
        {group.iconUrl && (
          <img src={group.iconUrl} alt="" width={28} height={28} className="size-7 shrink-0 rounded-md object-cover" />
        )}
        <div className="truncate font-display text-[0.9375rem] leading-tight">{group.name}</div>
      </div>
    </div>
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
      <h1 className="font-display" style={{ fontSize: 'calc(var(--text-base) + 3px)' }}>{title}</h1>

      {/* Nothing at all unless this deployment is a demo. */}
      <DemoMarker />

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

/** The foot of every page inside the app shell. */
export function Footer() {
  return (
    <footer
      className="mt-auto flex justify-end gap-4 border-t px-5 py-2 text-muted-foreground"
      style={{ borderTopWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <a href={DOCS_URL} target="_blank" rel="noreferrer" className="hover:text-foreground hover:underline">
        Docs
      </a>
      <a
        href={CREDITS_PATH}
        onClick={followLink(CREDITS_PATH)}
        className="hover:text-foreground hover:underline"
      >
        Credits
      </a>
    </footer>
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
            'flex items-center gap-1.5 rounded-md px-2 font-medium transition-colors',
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
