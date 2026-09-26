import { Button } from '@/components/ui/button'
import { DemoMarker } from '@/components/DemoMarker'
import { StatusRows } from '@/components/StatusRows'
import type { CurrentUser } from '@/lib/api'
import { CREDITS_PATH, GO_TO_KEYS, NAV, mayOpen, type NavItem, type PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import type { StatusRowId } from '@/lib/status'
import { IS_MAC, keyNames } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'
import { DOCS_URL } from '@/lib/docs'
import type { Density, Theme } from '@/lib/preferences'
import { followLink } from '@/lib/router'
import { Dialog as DialogPrimitive } from 'radix-ui'
import { Headset, LogOut, Menu, Moon, Rows3, Rows2, Search, Sun, UserRound, X, Zap } from 'lucide-react'
import { Kbd } from '@/components/ui/kbd'
import { SwitchBank } from '@/components/ui/switch-bank'
import { vrchatMedia } from '@/lib/vrchatMedia'

/** The group this Modbot manages, as the status endpoint reports it. */
export type SidebarGroup = { name: string; iconUrl: string | null; bannerUrl: string | null }

export function Sidebar({
  page,
  me,
  onNavigate,
  onSearch,
  onOpenHealth,
  group,
  badges,
  className,
  footer,
}: {
  page: PageId
  me: CurrentUser
  onNavigate: (p: PageId) => void
  /** Opens the command palette. */
  onSearch: () => void
  /** Opens the Health page at one part's card. */
  onOpenHealth: (section: StatusRowId) => void
  group?: SidebarGroup | null
  /** A count to show beside an entry -- open reviews beside Reviews. Zero or absent shows nothing. */
  badges?: Partial<Record<PageId, number>>
  className?: string
  /** Drawn at the foot, under Modbot's own mark. The phone sheet puts the top bar's controls here. */
  footer?: React.ReactNode
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
    <aside
      className={cn('flex flex-col overflow-y-auto border-r border-r-(length:--hairline) bg-background py-3', className)}
    >
      {/*
        The group at the top: its banner when VRChat has one, its icon and its name. This is the
        community's Modbot, and the sidebar says whose. Modbot's own mark moves to the foot.
      */}
      {group ? <GroupHeading group={group} /> : <ModbotHeading />}

      <button
        type="button"
        onClick={onSearch}
        className="mx-3 mb-3 flex h-(--control-h) shrink-0 items-center gap-2 rounded-sm border border-(length:--hairline) border-input bg-card px-2 text-muted-foreground hover:text-foreground"
      >
        <Search className="size-3.5 shrink-0" />
        <span className="flex-1 text-left">Search</span>
        <Kbd keys="mod+k" />
      </button>

      {rows.map(({ item, showGroup }) => (
        <div key={item.id}>
          {showGroup && 'group' in item && (
            <div
              className="flex items-center gap-2 pr-3 pb-1 pl-4 pt-4 font-label text-muted-foreground"
              style={{ fontSize: 'var(--text-small)' }}
            >
              {item.group}
              <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
            </div>
          )}
          <button
            onClick={() => onNavigate(item.id)}
            className={cn(
              'relative flex h-(--control-h) w-full items-center gap-2 pr-3 text-left transition-colors',
              // A page that belongs to the one above it, like Worlds under VRChat, sits one step in.
              'indent' in item && item.indent ? 'pl-8' : 'pl-4',
              page === item.id
                ? 'bg-card font-medium text-foreground'
                : 'text-muted-foreground hover:bg-card/60 hover:text-foreground',
            )}
          >
            {/* The rail's marker: a bar on the edge, not a filled pill. */}
            {page === item.id && <span aria-hidden className="absolute inset-y-0 left-0 w-0.5 bg-primary" />}
            <span className="min-w-0 flex-1 truncate">{item.label}</span>
            {badges?.[item.id] ? (
              <span
                className="rounded-sm bg-primary px-1 font-mono text-primary-foreground"
                style={{ fontSize: 'var(--text-tiny)', lineHeight: 1.5 }}
                aria-label={`${badges[item.id]} waiting`}
              >
                {badges[item.id]}
              </span>
            ) : null}
            {/* The go-to chord, where the page has one. Not on a phone, which has no keyboard. The
                keys side by side with no "then" between, which the palette's boxes have room for
                and a VR row with a long name does not. */}
            {GO_TO_KEYS[item.id] && (
              <span
                aria-hidden
                className="hidden shrink-0 font-mono text-muted-foreground/60 lg:inline"
                style={{ fontSize: 'var(--text-tiny)' }}
              >
                {keyNames(`g ${GO_TO_KEYS[item.id]}`, IS_MAC).join(' ')}
              </span>
            )}
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
        <div className="mt-auto flex items-center gap-2 px-4 pt-4">
          <img src="/icon-512.png" alt="" width={20} height={20} className="size-5 shrink-0" />
          <span className="font-display text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Modbot
          </span>
        </div>
      )}

      {footer && <div className={cn('px-3 pt-4', !group && 'mt-auto')}>{footer}</div>}
    </aside>
  )
}

function ModbotHeading() {
  return (
    <div className="flex items-center gap-2 px-4 pb-4">
      <img src="/icon-512.png" alt="" width={28} height={28} className="size-7 shrink-0" />
      <div className="font-display text-[0.9375rem] leading-none">Modbot</div>
    </div>
  )
}

function GroupHeading({ group }: { group: SidebarGroup }) {
  return (
    <div className="pb-4">
      {group.bannerUrl && (
        <img src={vrchatMedia(group.bannerUrl)} alt="" className="mx-3 mb-3 aspect-[3/1] w-[calc(100%-1.5rem)] rounded-sm object-cover" />
      )}
      <div className="flex items-center gap-2 px-4">
        {group.iconUrl && (
          <img src={vrchatMedia(group.iconUrl)} alt="" width={28} height={28} className="size-7 shrink-0 rounded-sm object-cover" />
        )}
        <div className="truncate font-display leading-tight" style={{ fontSize: 'calc(var(--text-base) + 2px)' }}>
          {group.name}
        </div>
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
      className="sticky top-0 z-10 flex items-center gap-3 border-b border-b-(length:--hairline) bg-background px-4 py-2.5 lg:px-5"
    >
      <h1 className="truncate font-display" style={{ fontSize: 'calc(var(--text-base) + 3px)' }}>{title}</h1>

      {/* Nothing at all unless this deployment is a demo. */}
      <DemoMarker />

      {/* Below the sidebar's breakpoint these five controls would leave no room for the title, so
          they move into the navigation sheet, which is one tap away at the foot of the screen. */}
      <div className="ml-auto hidden items-center gap-3 lg:flex">
        <AppearanceControls density={density} setDensity={setDensity} theme={theme} setTheme={setTheme} />

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
      </div>
    </header>
  )
}

/** Density and theme. In the top bar on a wide screen, in the navigation sheet on a phone. */
function AppearanceControls({
  density, setDensity, theme, setTheme,
}: {
  density: Density; setDensity: (d: Density) => void
  theme: Theme; setTheme: (t: Theme) => void
}) {
  return (
    <>
      <SwitchBank
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
        <span className="lg:sr-only">{theme === 'dark' ? 'Light theme' : 'Dark theme'}</span>
      </Button>
    </>
  )
}

/**
 * The sidebar as a sheet, for a screen too narrow to give it a column of its own.
 *
 * The same component, not a second navigation: one list of pages, one set of permission checks,
 * one place a page is added. It slides from the left because that is where the sidebar is on a
 * wide screen, and it is opened from the bar at the foot, where a thumb reaches.
 */
export function NavSheet({
  open,
  onOpenChange,
  nav,
  appearance,
  username,
  onAccount,
  onSignOut,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  nav: Omit<React.ComponentProps<typeof Sidebar>, 'className' | 'footer'>
  appearance: React.ComponentProps<typeof AppearanceControls>
  username?: string
  onAccount?: () => void
  onSignOut?: () => void
}) {
  const close = () => onOpenChange(false)

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 dark:bg-background/70 lg:hidden" />
        <DialogPrimitive.Content
          aria-describedby={undefined}
          className="fixed inset-y-0 left-0 z-50 flex w-[17rem] max-w-[85vw] flex-col border-r border-r-(length:--hairline) bg-background outline-none lg:hidden"
        >
          <DialogPrimitive.Title className="sr-only">Pages</DialogPrimitive.Title>
          <DialogPrimitive.Close
            className="absolute top-3 right-3 z-10 grid place-items-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground"
            style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
            aria-label="Close"
          >
            <X className="size-5" />
          </DialogPrimitive.Close>

          <Sidebar
            {...nav}
            className="min-h-0 flex-1 border-r-0 pb-[max(1rem,env(safe-area-inset-bottom))]"
            onNavigate={(p) => {
              close()
              nav.onNavigate(p)
            }}
            onSearch={() => {
              close()
              nav.onSearch()
            }}
            onOpenHealth={(section) => {
              close()
              nav.onOpenHealth(section)
            }}
            footer={
              <div className="flex flex-col items-start gap-2 border-t border-t-(length:--hairline) pt-4">
                <AppearanceControls {...appearance} />
                {onAccount && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => {
                      close()
                      onAccount()
                    }}
                  >
                    <UserRound className="size-4" />
                    <span className="max-w-[9rem] truncate">{username ?? 'Your account'}</span>
                  </Button>
                )}
                {onSignOut && (
                  <Button variant="ghost" size="sm" onClick={onSignOut}>
                    <LogOut className="size-4" />
                    Sign out
                  </Button>
                )}
              </div>
            }
          />
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  )
}

/**
 * The bar at the foot of the screen on a phone.
 *
 * At the foot rather than the top because a phone held in one hand puts the top of a tall screen
 * out of a thumb's reach, and these are the three controls a moderator reaches for most: the
 * pages, a person by name, and whatever the screen they are on can do.
 *
 * "Actions" is the answer to the keyboard. Every key a page registers carries a label already
 * (lib/shortcuts.ts), so the sheet that lists them for `?` is also the list of what the page can
 * do -- and each row runs it (components/ShortcutSheet.tsx). It leaves out "Go to", because that
 * is what Menu is.
 */
export function BottomBar({
  onMenu,
  onSearch,
  onThisPage,
}: {
  onMenu: () => void
  onSearch: () => void
  onThisPage: () => void
}) {
  return (
    <nav
      className="fixed inset-x-0 bottom-0 z-30 flex divide-x-(--hairline) divide-border border-t border-t-(length:--hairline) bg-background pb-[env(safe-area-inset-bottom)] lg:hidden"
    >
      <BottomButton icon={<Menu className="size-5" />} label="Menu" onClick={onMenu} />
      <BottomButton icon={<Search className="size-5" />} label="Search" onClick={onSearch} />
      <BottomButton icon={<Zap className="size-5" />} label="Actions" onClick={onThisPage} />
    </nav>
  )
}

function BottomButton({ icon, label, onClick }: { icon: React.ReactNode; label: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="flex flex-1 flex-col items-center justify-center gap-0.5 py-1.5 text-muted-foreground active:bg-muted"
      style={{ minHeight: 'var(--control-h)' }}
    >
      {icon}
      <span style={{ fontSize: 'var(--text-tiny)' }}>{label}</span>
    </button>
  )
}

/** The foot of every page inside the app shell. */
export function Footer() {
  return (
    <footer
      className="mt-auto flex justify-end gap-4 border-t border-t-(length:--hairline) px-5 py-2 text-muted-foreground"
      style={{ fontSize: 'var(--text-small)' }}
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
