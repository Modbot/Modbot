// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and who may open which page is
// worth a test. `permissions.ts` imports nothing at run time, so it loads as it is too.
import type { CurrentUser } from './api.ts'
import { can, canAny } from './permissions.ts'

// A count beside a label is only ever one read from the server: open reviews, open flags, their
// total beside Now, and flagged people in an instance beside Live (App.tsx). The prototype showed
// a hardcoded "14,208" next to Members, and a made-up number in a running deployment is
// indistinguishable from a real one.
//
// Each entry names the permission it needs. The sidebar hides what the person cannot open; the
// server refuses the data regardless (accounts and access design §8). Permission names, never
// bits -- see lib/permissions.ts for why.
export const NAV = [
  // The front page (UX review 2026-09-25, finding 3): what is waiting for a decision, who is in the
  // group's instances, Modbot's health and what changed since this person last looked. Every part
  // is read under its own page's permission, so it needs none of its own.
  { id: 'now', label: 'Now' },
  // The people asking to be let in, read from VRChat when the page is opened. Near People
  // because it is the same roster one step earlier.
  { id: 'requests', label: 'Requests', needs: 'ViewJoinRequests' },
  // The Discord server's own member list. Separate from Members, because most people are on one
  // side only and most never link. Not in the page list since 2026-09-27: it is the Members link on
  // the Discord page's header, so the sidebar lights Discord while it is open. The palette and
  // `g d` still reach it, and its address is unchanged.
  { id: 'discord-members', label: 'Discord members', needs: 'ViewMembers', hidden: true, under: 'analytics-server' },
  // Everyone Modbot has a record of, not only the group's roster: the people it has seen in an
  // instance or read about in the audit log have a profile and a history too, and no list led to
  // them. "People" rather than "Users", which is the settings screen for Modbot's own accounts.
  // See members opens it too, for the Members view alone (below).
  { id: 'people', label: 'People', needsAny: ['ViewProfile', 'ViewMembers'] },
  // Not a page since 2026-09-27: the member list became People narrowed to members, with its
  // columns and filters (`MEMBERS_PATH`). It keeps its name so the VRChat page's Members tab, `g m`
  // and the palette still go to it, and the sidebar lights People while it is open. `/members`
  // and its old filters still open it (`membersAddress`).
  { id: 'members', label: 'Members', needs: 'ViewMembers', hidden: true, under: 'people' },
  // The group's open instances right now and who is in each.
  { id: 'live', label: 'Live', needs: 'ViewLiveInstances' },
  // Planned events, where each is published, and the calendar feed (calendar design).
  { id: 'calendar', label: 'Calendar', needs: 'ViewCalendar' },
  // Giveaways, their rules, who entered and how each draw went (giveaways design).
  { id: 'giveaways', label: 'Giveaways', needs: 'ViewGiveaways' },
  // Questions answered from Modbot's own data, with tools that run as the person asking.
  { id: 'chat', label: 'Chat', needs: 'UseAiChat' },
  { id: 'bans', label: 'Bans', needs: 'ViewAuditLog' },
  // What AI moderation rules flagged. A flag is a note about a person, so it needs ViewProfile;
  // dismissing one needs ReviewTickets, which the page checks for itself.
  { id: 'flags', label: 'Flags', needs: 'ViewProfile' },
  // Reviews of a moderator's pattern (spec 5.8.5), beside Flags because both are things somebody
  // has to look at and decide. Gated on ReviewTickets because the people being reviewed should
  // not be closing them. It used to sit under a "Team" heading of its own, which named a different
  // thing from the Analytics page called Team.
  { id: 'reviews', label: 'Reviews', needs: 'ReviewTickets' },
  { id: 'audit', label: 'Audit log', needsAny: ['ViewAuditLog', 'ViewOperationalLog'] },
  // One page per question (spec 10.1), not one "metrics" page. Tracked Groups is a later
  // feature (spec 10.3) and has no entry until it exists. Named by what each is about: the team,
  // then each platform. Worlds is VRChat's, so it sits indented under it; the ids and addresses
  // keep their old names, so links and bookmarks still open the same pages.
  { id: 'analytics-team', label: 'Team', group: 'Analytics', needs: 'ViewAnalytics' },
  { id: 'analytics-group', label: 'VRChat', needs: 'ViewAnalytics' },
  { id: 'analytics-worlds', label: 'Worlds', indent: true, needs: 'ViewAnalytics' },
  // Not in the page list since 2026-09-27: it is the Instances tab of the VRChat page, the way
  // vrchat.com shows a group's instances, so the sidebar lights VRChat while it is open. The
  // palette and `g i` still reach it, and its address is unchanged.
  { id: 'analytics-instances', label: 'Instances', needs: 'ViewAnalytics', hidden: true, under: 'analytics-group' },
  // The VRChat page's Posts and Settings tabs, at addresses of their own under the page's
  // (`/analytics/group/posts`) so a tab can be linked to. Reading posts is part of reading the
  // page; Settings is only for changing the group, so it needs the permission that does.
  { id: 'group-posts', label: 'VRChat posts', needs: 'ViewAnalytics', hidden: true, under: 'analytics-group' },
  { id: 'group-settings', label: 'VRChat settings', needs: 'EditGroupProfile', hidden: true, under: 'analytics-group' },
  // The Discord server, beside the group: its own members, messages and voice (M5 spec §6).
  { id: 'analytics-server', label: 'Discord', needs: 'ViewAnalytics' },
  // Not in the page list: the status rows at the foot of the sidebar say what it says, and each
  // one opens it at the part it names. The page, its address and every link to it are unchanged.
  // Called "Sync health" until 2026-09-26; it covers far more than syncing.
  { id: 'health', label: 'Health', needs: 'ViewOperationalLog', hidden: true },
  // Modbot's own program log. "Logs" alone collided with the person popup's tab and with the
  // audit log; the id and address stay `logs`.
  { id: 'logs', label: "Modbot's log", group: 'System', needs: 'ViewOperationalLog' },
  // Users and Roles are tabs inside Settings (the IAM tab), so somebody who may manage either but
  // not the settings themselves still needs the page to open. Which tabs they see is the page's
  // own check.
  { id: 'settings', label: 'Settings', group: 'System', needsAny: ['ManageSettings', 'ManageUsers', 'ManageRoles'] },
  { id: 'account', label: 'Your account', hidden: true },
  // Reached from the Bans page and the subject pane, not from the sidebar. The server gates
  // reads on ViewProfile and writes on Ban; the page shows the refusal in words.
  { id: 'cases', label: 'Case files', hidden: true },
  // Reached from the footer and from Settings. No requirement: everyone signed in may read it.
  { id: 'credits', label: 'Credits', hidden: true },
] as const

/**
 * Credits lives under Settings, and is open to everybody signed in.
 *
 * Its address says "settings" only because that is where the tabs are; the permission that gates
 * the Settings page is on that page's own entry above, never on a path prefix, so this one is not
 * caught by it.
 */
export const CREDITS_PATH = '/settings/credits'

/** Settings' IAM tab: Modbot's own accounts and the roles they hold. */
export const IAM_PATH = '/settings#iam'

/**
 * Addresses that moved. The old one still opens the page -- the footer and the Deployment card
 * pointed at `/credits` for months and so does anything anyone bookmarked -- and the URL is
 * quietly replaced with the new one.
 *
 * Users and Roles stopped being pages of their own on 2026-09-18 and became the two halves of
 * Settings' IAM tab. Every link to them, in a message or a bookmark, still lands on the half it
 * named.
 */
export const MOVED: Record<string, string> = {
  '/credits': CREDITS_PATH,
  '/users': `${IAM_PATH}/users`,
  '/roles': `${IAM_PATH}/roles`,
}

export type NavItem = (typeof NAV)[number]

export type PageId = NavItem['id']

/**
 * The letter after `g` that goes to each page (Linear's `g` then a letter, research 2026-09-16).
 *
 * One letter per page, and the sheet on `?` lists them, so the choice only has to be stable, not
 * guessable. Pages this person may not open are not registered at all; an empty letter is a page
 * with no chord, reached from elsewhere.
 */
export const GO_TO_KEYS: Record<PageId, string> = {
  now: 'k',
  members: 'm',
  requests: 'j',
  'discord-members': 'd',
  people: 'n',
  live: 'l',
  calendar: 'e',
  giveaways: 'p',
  chat: 'c',
  bans: 'b',
  flags: 'f',
  audit: 'a',
  'analytics-group': 'g',
  'analytics-server': 'v',
  'analytics-team': 't',
  'analytics-worlds': 'w',
  'analytics-instances': 'i',
  'group-posts': '',
  'group-settings': '',
  reviews: 'r',
  health: 'h',
  logs: 'o',
  settings: 's',
  account: 'y',
  cases: '',
  credits: '',
}

/**
 * The sidebar entry lit while a page is open: the page's own, or, for a page shown as part of
 * another (`under`), that one's. Discord members is the Members link on the Discord page, so
 * Discord stays lit; the VRChat page's Instances, Posts and Settings tabs light VRChat.
 */
export function sidebarEntry(id: PageId): PageId {
  const item = NAV.find((n) => n.id === id)
  return item && 'under' in item ? item.under : id
}

/**
 * Whether a page is offered by name, in the palette and as a `g` chord: every page in the
 * sidebar, and a page off it that is shown as part of one that is. Other pages off the sidebar
 * are reached from somewhere in particular -- a case file from Bans -- and have no name to go to.
 */
export function goesByName(item: NavItem): boolean {
  return !('hidden' in item && item.hidden) || 'under' in item
}

/** Whether this person may open a page. Pages with no requirement are open to everyone signed in. */
export function mayOpen(me: CurrentUser, id: PageId): boolean {
  const item = NAV.find((n) => n.id === id)
  if (!item) return true
  if ('needsAny' in item) return canAny(me, item.needsAny)
  if ('needs' in item) return can(me, item.needs)
  return true
}

/**
 * The counts beside the sidebar's entries, added up: how much is waiting for somebody, which the
 * browser tab's title carries so a tab in the background still says so. Only the counts this
 * person is shown are passed in, so the total never includes a queue they cannot open.
 */
export function waitingTotal(badges: Partial<Record<PageId, number>>): number {
  let total = 0
  for (const count of Object.values(badges)) {
    if (typeof count === 'number' && Number.isFinite(count) && count > 0) total += Math.floor(count)
  }
  return total
}

/**
 * The Members view: People narrowed to the group's current members. The chip is `MEMBERS_VIEW` of
 * lib/pageFilters.ts, spelled out because that file does not load in Node.
 */
export const MEMBERS_PATH = '/people?f=membership%3Ais%3Amember'

/**
 * Where an address that meant the member list goes now that it is a view of People.
 *
 * The member list lived at `/` until Now took it, then at `/members` until People took it in, and
 * wrote its filters and its page into the address (`/?f=status:is:current`: the `PARAM`s of
 * lib/filters.ts and lib/listPage.ts, spelled out because neither loads in Node). Those addresses
 * were copied into messages and bookmarks for months, and unusual-activity alerts linked to
 * `/?joinedFrom=…`. So `/members` with anything after it, and a `/` carrying the list's filters,
 * page or join stretch, go to People with everything else in the address kept.
 *
 * The member list's filters are People's under the same names, except its Status: "Members" and
 * "People who left" are People's Membership chip, and its "all" is no Membership chip at all. An
 * address with no filters opened the list on current members, and still does.
 *
 * Null for every other address, including a plain `/` and a `/?subject=…`, which are Now's.
 */
export function membersAddress(path: string, search: string): string | null {
  const params = new URLSearchParams(search)

  if (path === '/') {
    if (!params.has('f') && !params.has('page') && !params.has('joinedFrom')) return null
  } else if (path !== '/members') {
    return null
  }

  const said = params.getAll('f')
  params.delete('f')

  const chips = said.length === 0 ? ['membership:is:member'] : said.filter((chip) => chip !== '').map(fromStatus).filter((chip) => chip !== null)

  // "None" is written as one empty `f`, so the page does not fill in the filters it last used.
  if (chips.length === 0) params.append('f', '')
  for (const chip of chips) params.append('f', chip)

  return `/people?${params.toString()}`
}

/** One member list chip as People's: its Status as Membership, "all" as nothing, the rest as they were. */
function fromStatus(chip: string): string | null {
  if (!chip.startsWith('status:')) return chip
  if (chip === 'status:is:current') return 'membership:is:member'
  if (chip === 'status:is:left') return 'membership:is:left'
  return null
}

/** `(3) Modbot` while something is waiting, and the title as it was when nothing is. */
export function titleWithCount(title: string, count: number): string {
  return count > 0 ? `(${count}) ${title}` : title
}
