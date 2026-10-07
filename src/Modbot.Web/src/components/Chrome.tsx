import { Button } from '@/components/ui/button'
import { DemoMarker } from '@/components/DemoMarker'
import { StatusRows } from '@/components/StatusRows'
import type { CurrentUser } from '@/lib/api'
import { CREDITS_PATH, GO_TO_KEYS, menuPages, sidebarEntry, sidebarRows, type PageId } from '@/lib/nav'
import { countText } from '@/lib/joinRequests'
import { can } from '@/lib/permissions'
import { DOT, TONE, statusLine, type StatusRowId } from '@/lib/status'
import { useStatusRows } from '@/lib/useStatusRows'
import { usePhoneLayout } from '@/lib/phoneLayout'
import { SHEET, useMedia } from '@/components/calendar/phone'
import { DialogContent } from '@/components/ui/dialog'
import { hasPageActions, useShortcutList, useWaitingChord } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'
import { DOCS_URL } from '@/lib/docs'
import { ISSUES_LABEL, ISSUES_URL } from '@/lib/issues'
import type { Place, Theme } from '@/lib/preferences'
import { followLink } from '@/lib/router'
import { Dialog as DialogPrimitive } from 'radix-ui'
import {
  Ban, Bug, CalendarClock, CalendarDays, ChartLine, ChevronRight, Circle, ClipboardCheck, Flag, Gift, Globe, Hash, Headset,
  House, Logs, LogOut, Megaphone, Menu, MessageSquare, Monitor, Moon, Plug, Radio, ScrollText, Search, Settings, Shuffle, Sun,
  UserPlus, UserRound, Users, X, Zap, type LucideIcon,
} from 'lucide-react'
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
  alarms,
  more,
  className,
  footer,
}: {
  page: PageId
  me: CurrentUser
  onNavigate: (p: PageId) => void
  /** Opens the command palette. */
  onSearch: () => void
  /** Opens the Health page at one part's card, or at its top. */
  onOpenHealth: (section: StatusRowId | null) => void
  group?: SidebarGroup | null
  /** A count to show beside an entry -- open reviews beside Reviews. Zero or absent shows nothing. */
  badges?: Partial<Record<PageId, number>>
  /**
   * Entries whose count is somebody in an instance right now rather than a queue, drawn in the
   * destructive colour: flagged people beside Live.
   */
  alarms?: Partial<Record<PageId, boolean>>
  /** Entries whose count came from a full page of a list with no total, written `50+`. */
  more?: Partial<Record<PageId, boolean>>
  className?: string
  /** Drawn at the foot, under Modbot's own mark. The phone sheet puts the top bar's controls here. */
  footer?: React.ReactNode
}) {
  // The headings and the pages, each heading before the first page of its own this person sees
  // (lib/nav.ts `sidebarRows`).
  const rows = sidebarRows(me)
  // A page shown as part of another, like Discord members on the Discord page, lights that one.
  const lit = sidebarEntry(page)
  const goToWaiting = useWaitingChord() === 'g'

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

      {rows.map((row) => {
        if (row.kind === 'heading') {
          const opens = row.page
          const heading = 'flex w-full shrink-0 items-center gap-2 pr-3 pb-1 pl-4 pt-4 font-label'

          // A heading that is a page of its own (Integrations) is its link, with the chevron a
          // link to a page carries; the rest are only names.
          return opens ? (
            <button
              key={`heading:${row.label}`}
              type="button"
              onClick={() => onNavigate(opens)}
              aria-current={lit === opens ? 'page' : undefined}
              className={cn(
                heading,
                'text-left transition-colors',
                lit === opens ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
              )}
              style={{ fontSize: 'var(--text-small)' }}
            >
              <span className="flex items-center gap-0.5">
                {row.label}
                <ChevronRight className="size-3.5 shrink-0" aria-hidden />
              </span>
              <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
            </button>
          ) : (
            <div
              key={`heading:${row.label}`}
              className={cn(heading, 'text-muted-foreground')}
              style={{ fontSize: 'var(--text-small)' }}
            >
              {row.label}
              <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
            </div>
          )
        }

        const { item } = row
        return (
          // `shrink-0`: a row is a control high however long the list, as it was inside a wrapper.
          <button
            key={item.id}
            type="button"
            onClick={() => onNavigate(item.id)}
            className={cn(
              'group/row relative flex h-(--control-h) w-full shrink-0 items-center gap-2 pr-3 text-left transition-colors',
              // A page that belongs to the one above it, like Worlds under VRChat, sits one step in.
              'indent' in item && item.indent ? 'pl-8' : 'pl-4',
              lit === item.id
                ? 'bg-card font-medium text-foreground'
                : 'text-muted-foreground hover:bg-card/60 hover:text-foreground',
            )}
          >
            {/* The rail's marker: a bar on the edge, not a filled pill. */}
            {lit === item.id && <span aria-hidden className="absolute inset-y-0 left-0 w-0.5 bg-primary" />}
            <span className="min-w-0 flex-1 truncate">{item.label}</span>
            <CountMark count={badges?.[item.id]} alarm={alarms?.[item.id]} more={more?.[item.id]} />
            {/* The go-to chord, where the page has one. Not on a phone or in a headset, which have no keyboard to hand. The
                keys side by side with no "then" between, which the palette's boxes have room for
                and a VR row with a long name does not. Shown on the row under the pointer or the
                keyboard's focus, and on every row while `g` waits for its second key; the rest of
                the time 34 boxes beside 17 labels only compete with them. Hidden, not removed, so
                a badge does not jump when they appear. */}
            {GO_TO_KEYS[item.id] && (
              <span aria-hidden className="contents">
                <Kbd
                  keys={`g ${GO_TO_KEYS[item.id]}`}
                  compact
                  className={cn(
                    'shrink-0',
                    !goToWaiting && 'invisible group-hover/row:visible group-focus-visible/row:visible',
                  )}
                />
              </span>
            )}
          </button>
        )
      })}

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

/** The count beside a page's name, in the sidebar and on the phone's page tiles. Nothing for zero. */
function CountMark({
  count,
  alarm,
  more,
  className,
}: {
  count?: number
  alarm?: boolean
  more?: boolean
  className?: string
}) {
  if (!count) return null
  const text = countText(count, more === true)
  return (
    <span
      className={cn(
        'rounded-sm px-1 font-mono',
        alarm ? 'bg-destructive text-destructive-foreground' : 'bg-primary text-primary-foreground',
        className,
      )}
      style={{ fontSize: 'var(--text-tiny)', lineHeight: 1.5 }}
      aria-label={alarm ? `${count} flagged here` : `${text} waiting`}
    >
      {text}
    </span>
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
  title, place, setPlace, theme, setTheme, username, onAccount, onSignOut,
}: {
  title: string
  place: Place; setPlace: (p: Place) => void
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
        <AppearanceControls place={place} setPlace={setPlace} theme={theme} setTheme={setTheme} />

        <IssuesButton />

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
          <Button variant="ghost" size="sm" onClick={onSignOut} title="Sign out" aria-label="Sign out">
            <LogOut className="size-4" />
          </Button>
        )}
      </div>
    </header>
  )
}

/**
 * Bugs and feedback, in the top bar on a wide screen and in the navigation sheet on a phone and in a
 * headset: somewhere a moderator sees it on every page without looking for it.
 */
function IssuesButton() {
  return (
    <Button variant="ghost" size="sm" asChild>
      <a href={ISSUES_URL} target="_blank" rel="noreferrer">
        <Bug className="size-4" />
        {ISSUES_LABEL}
      </a>
    </Button>
  )
}

/**
 * Where you are and the theme. In the top bar on a wide screen, in the navigation sheet on a phone
 * and in a headset. A desk's density is set once, so it is in Your account instead.
 */
function AppearanceControls({
  place, setPlace, theme, setTheme,
}: {
  place: Place; setPlace: (p: Place) => void
  theme: Theme; setTheme: (t: Theme) => void
}) {
  return (
    <>
      <SwitchBank
        label="Where you are"
        value={place}
        onChange={setPlace}
        options={[
          { value: 'desk', label: 'Desk', icon: <Monitor className="size-3.5" /> },
          { value: 'headset', label: 'Headset', icon: <Headset className="size-3.5" /> },
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
 *
 * On a phone, upright or on its side, it is a grid of pages rising from the bottom instead
 * (`PageGrid`). The drawer ran 1.7 screens tall with its top half out of a thumb's reach (mobile
 * review 2026-09-28). A tablet keeps the drawer, which fits it, and so does a headset, which is also
 * the one place to switch back from Headset to Desk.
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
  // A phone: where a dialog is a sheet from the bottom, and too small for a popup's two columns.
  // Both halves are tests the app already makes, so this adds no third idea of what a phone is.
  const sheet = useMedia(SHEET)
  const small = usePhoneLayout()
  const grid = sheet && small && appearance.place !== 'headset'

  // One Root for both, so crossing from one to the other with Menu open (a window resized past a
  // tablet's width) swaps what is drawn rather than taking the dialog down and building it again.
  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      {grid ? (
        <PageGrid
          nav={nav}
          appearance={appearance}
          username={username}
          onAccount={onAccount}
          onSignOut={onSignOut}
          close={close}
        />
      ) : (
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 dark:bg-background/70 desk:lg:hidden" />
          <DialogPrimitive.Content
            aria-describedby={undefined}
            className="fixed inset-y-0 left-0 z-50 flex w-[17rem] max-w-[85vw] flex-col border-r border-r-(length:--hairline) bg-background outline-none desk:lg:hidden"
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
                  <IssuesButton />
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
      )}
    </DialogPrimitive.Root>
  )
}

/** Each listed page's picture on the phone's page tiles. A page added without one gets a plain dot. */
const PAGE_ICONS: Partial<Record<PageId, LucideIcon>> = {
  now: House,
  chat: MessageSquare,
  stats: ChartLine,
  requests: UserPlus,
  people: Users,
  live: Radio,
  bans: Ban,
  flags: Flag,
  reviews: ClipboardCheck,
  audit: ScrollText,
  calendar: CalendarDays,
  'world-lists': Shuffle,
  marketing: Megaphone,
  giveaways: Gift,
  availability: CalendarClock,
  integrations: Plug,
  'analytics-group': Globe,
  'analytics-server': Hash,
  logs: Logs,
  settings: Settings,
}

/**
 * Menu on a phone: every page as a tile in one grid, on the app's sheet from the bottom, with
 * Modbot's health, the theme, bugs and feedback, the account and Sign out in one row under it.
 *
 * It fits one screen: three tiles across held upright and as many as fit on its side, so nothing
 * is out of a thumb's reach and nothing scrolls. What the drawer carried and this leaves out:
 * Search, which is on the bottom bar under it; the headings, since the pages keep their order and
 * so their groups, except Integrations, which is a page and so a tile; the four status rows, said in
 * the one line Now says them in; and Desk or Headset, which a phone has no use for. The group is named in the sheet's header, by its icon and
 * name, and its banner is left out.
 */
function PageGrid({
  nav,
  appearance,
  username,
  onAccount,
  onSignOut,
  close,
}: {
  nav: Omit<React.ComponentProps<typeof Sidebar>, 'className' | 'footer'>
  appearance: React.ComponentProps<typeof AppearanceControls>
  username?: string
  onAccount?: () => void
  onSignOut?: () => void
  close: () => void
}) {
  const { me, group, badges, alarms, more } = nav
  const lit = sidebarEntry(nav.page)
  const { theme, setTheme } = appearance
  const closeThen = (run: () => void) => () => {
    close()
    run()
  }

  return (
    <DialogContent
      aria-describedby={undefined}
      title={group?.name ?? 'Pages'}
      lead={
        group?.iconUrl ? (
          <img src={vrchatMedia(group.iconUrl)} alt="" width={28} height={28} className="size-7 shrink-0 rounded-sm object-cover" />
        ) : undefined
      }
      bodyClassName="px-2 py-2 short:py-1"
      foot={
        <div className="flex shrink-0 items-center gap-1 border-t border-t-(length:--hairline) px-2 py-1 pb-[max(0.25rem,env(safe-area-inset-bottom))]">
          {/* The same line as the sidebar's status rows, for the same people. */}
          {can(me, 'ViewOperationalLog') ? (
            <HealthButton
              onOpen={(section) => {
                close()
                nav.onOpenHealth(section)
              }}
            />
          ) : (
            <span className="flex-1" />
          )}
          <Button
            variant="ghost"
            size="icon"
            onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
            aria-label={theme === 'dark' ? 'Light theme' : 'Dark theme'}
          >
            {theme === 'dark' ? <Sun className="size-5" /> : <Moon className="size-5" />}
          </Button>
          <Button variant="ghost" size="icon" asChild>
            <a href={ISSUES_URL} target="_blank" rel="noreferrer" aria-label={ISSUES_LABEL}>
              <Bug className="size-5" />
            </a>
          </Button>
          {onAccount && (
            <Button variant="ghost" size="icon" onClick={closeThen(onAccount)} aria-label={username ?? 'Your account'}>
              <UserRound className="size-5" />
            </Button>
          )}
          {onSignOut && (
            <Button variant="ghost" size="icon" onClick={onSignOut} aria-label="Sign out">
              <LogOut className="size-5" />
            </Button>
          )}
        </div>
      }
    >
      {/* A phone on its side is under 400px tall. The tiles lose their padding there, so three rows
          of them still fit on one screen, six or more across from a 667px-wide phone up. */}
      <ul className="grid grid-cols-[repeat(auto-fill,minmax(6.5rem,1fr))] gap-1">
        {menuPages(me).map((item) => {
          const Icon = PAGE_ICONS[item.id] ?? Circle
          const here = lit === item.id
          return (
            <li key={item.id}>
              <button
                type="button"
                onClick={closeThen(() => nav.onNavigate(item.id))}
                aria-current={here ? 'page' : undefined}
                className={cn(
                  'relative flex min-h-[calc(var(--control-h)+0.75rem)] w-full flex-col items-center justify-center gap-1 overflow-hidden rounded-sm px-1 py-2',
                  'short:min-h-(--control-h) short:gap-0 short:py-1',
                  'outline-none focus-visible:outline-2 focus-visible:outline-solid focus-visible:-outline-offset-2 focus-visible:outline-ring',
                  here ? 'bg-background font-medium text-foreground' : 'text-muted-foreground active:bg-muted',
                )}
              >
                {/* At the foot, clear of the count on the icon's corner. */}
                {here && <span aria-hidden className="absolute inset-x-0 bottom-0 h-0.5 bg-primary" />}
                <span className="relative shrink-0">
                  <Icon className="size-5" />
                  <CountMark
                    count={badges?.[item.id]}
                    alarm={alarms?.[item.id]}
                    more={more?.[item.id]}
                    className="absolute -top-2 left-[calc(100%-0.25rem)] short:-top-1"
                  />
                </span>
                <span className="w-full truncate text-center" style={{ fontSize: 'var(--text-small)' }}>
                  {item.label}
                </span>
              </button>
            </li>
          )
        })}
      </ul>
    </DialogContent>
  )
}

/** Modbot's health in the one line Now says it in, opening the Health page at the first problem. */
function HealthButton({ onOpen }: { onOpen: (section: StatusRowId | null) => void }) {
  const line = statusLine(useStatusRows())

  return (
    <button
      type="button"
      onClick={() => onOpen(line.section)}
      className="flex min-h-(--control-h) min-w-0 flex-1 items-center gap-2 rounded-sm px-2 text-left outline-none active:bg-muted focus-visible:outline-2 focus-visible:outline-solid focus-visible:-outline-offset-2 focus-visible:outline-ring"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <span aria-hidden className={cn('size-2.5 shrink-0', DOT[line.tone])} />
      <span className={cn('truncate', line.tone === 'ok' ? 'text-muted-foreground' : TONE[line.tone])}>{line.text}</span>
    </button>
  )
}

/**
 * The bar at the foot of the screen on a phone and in a headset.
 *
 * At the foot rather than the top because a phone held in one hand puts the top of a tall screen
 * out of a thumb's reach, and these are the controls a moderator reaches for most: the pages, a
 * person by name, what is waiting, and whatever the screen they are on can do. A headset gets it
 * for the same controls, at the headset's larger size, in place of a sidebar whose labels do not fit.
 *
 * Now is here with its count because it is the page a moderator comes back to between everything
 * else, and the count says whether coming back is worth it without opening Menu.
 *
 * "Actions" is the answer to the keyboard. Every key a page registers carries a label already
 * (lib/shortcuts.ts), so the sheet that lists them for `?` is also the list of what the page can
 * do -- and each row runs it (components/ShortcutSheet.tsx). It leaves out the app's own keys,
 * which Menu and Search already are, and so is drawn only on a page that registered keys of its
 * own: on the rest it opened a sheet of keyboard help (review 2026-09-27, finding 8).
 */
export function BottomBar({
  page,
  waiting,
  onMenu,
  onSearch,
  onNow,
  onActions,
}: {
  page: PageId
  waiting: number
  onMenu: () => void
  onSearch: () => void
  onNow: () => void
  onActions: () => void
}) {
  const actions = hasPageActions(useShortcutList())

  return (
    <nav
      className="fixed inset-x-0 bottom-0 z-30 flex divide-x-(--hairline) divide-border border-t border-t-(length:--hairline) bg-background pb-[env(safe-area-inset-bottom)] desk:lg:hidden"
    >
      <BottomButton icon={<Menu className="size-5 headset:size-7" />} label="Menu" onClick={onMenu} />
      <BottomButton icon={<Search className="size-5 headset:size-7" />} label="Search" onClick={onSearch} />
      <BottomButton
        icon={<House className="size-5 headset:size-7" />}
        label="Now"
        count={waiting}
        current={page === 'now'}
        onClick={onNow}
      />
      {actions && <BottomButton icon={<Zap className="size-5 headset:size-7" />} label="Actions" onClick={onActions} />}
    </nav>
  )
}

function BottomButton({
  icon,
  label,
  count,
  current,
  onClick,
}: {
  icon: React.ReactNode
  label: string
  count?: number
  current?: boolean
  onClick: () => void
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={current ? 'page' : undefined}
      className={cn(
        'flex flex-1 flex-col items-center justify-center gap-0.5 py-1.5 active:bg-muted',
        current ? 'text-foreground' : 'text-muted-foreground',
      )}
      style={{ minHeight: 'var(--control-h)' }}
    >
      {icon}
      <span className="flex items-center gap-1" style={{ fontSize: 'var(--text-tiny)' }}>
        {label}
        {/* The same mark as the sidebar's, so the number reads the same in both places. */}
        {count ? (
          <span
            className="rounded-sm bg-primary px-1 font-mono text-primary-foreground"
            style={{ lineHeight: 1.5 }}
            aria-label={`${count} waiting`}
          >
            {count}
          </span>
        ) : null}
      </span>
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
      <a href={ISSUES_URL} target="_blank" rel="noreferrer" className="hover:text-foreground hover:underline">
        {ISSUES_LABEL}
      </a>
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
