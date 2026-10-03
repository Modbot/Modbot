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
//
// `words` are what else a page answers to in the palette (site review 2026-09-27, finding 3): the
// word a moderator types is often not the label -- "join" for Requests, "banned" for Bans -- and
// without them the palette said "Nothing matches" or offered a person whose name held the word.
export const NAV = [
  // The front page (UX review 2026-09-25, finding 3): what is waiting for a decision, who is in the
  // group's instances, Modbot's health and what changed since this person last looked. Every part
  // is read under its own page's permission, so it needs none of its own.
  { id: 'now', label: 'Now' },
  // Questions answered from Modbot's own data, with tools that run as the person asking. Beside
  // Now with no heading since 2026-09-27: it asks about every part of the app, not one of them.
  // Offered only while AI chat is on (`on`, since 2026-09-29): before that the sidebar showed Chat
  // to anybody with the permission, and with AI chat off the page it led to only said "Chat is
  // off." A direct link still opens the page, which still says so.
  { id: 'chat', label: 'Chat', needs: 'UseAiChat', on: 'chatOn' },
  // Stats: the charts of every platform on one page, a tab per question (Stats page design, spec
  // 10.1). It took Team's place on 2026-09-27, when Team and Worlds became parts of its Moderation
  // and Activity tabs; their old addresses open those tabs (`MOVED`), and the palette still finds
  // them by name. The Growth tab is the page itself; the other two light Stats. Tracked Groups is
  // a later feature (spec 10.3) and has no entry until it exists. Beside Now and Chat with no heading
  // since 2026-09-27: it charts every platform, and once VRChat and Discord moved to Integrations the
  // Analytics heading would have stood over it alone.
  { id: 'stats', label: 'Stats', needs: 'ViewAnalytics', words: ['growth'] },
  { id: 'stats-activity', label: 'Activity stats', needs: 'ViewAnalytics', hidden: true, under: 'stats', words: ['worlds'] },
  { id: 'stats-moderation', label: 'Moderation stats', needs: 'ViewAnalytics', hidden: true, under: 'stats', words: ['team'] },
  // The people asking to be let in, read from VRChat when the page is opened. Near People
  // because it is the same roster one step earlier.
  //
  // "Community" heads the pages about the group's people, from here down to Lists
  // (2026-09-27). Before it the first eleven entries ran on with no heading, Chat in the middle.
  // Inside it, the pages that act on a person come first and Bans follows Live, where a
  // moderator most often decides one; Calendar, Giveaways and Lists, opened less often, close it. Each
  // of them names the heading, so it is still drawn for somebody who may not open Requests.
  { id: 'requests', label: 'Requests', group: 'Community', needs: 'ViewJoinRequests', words: ['join', 'join requests', 'applicants'] },
  // The Discord server's own member list. Separate from Members, because most people are on one
  // side only and most never link. Not in the page list since 2026-09-27: it is the Members link on
  // the Discord page's header, so the sidebar lights Discord while it is open. The palette and
  // `g d` still reach it, and its address is unchanged.
  { id: 'discord-members', label: 'Discord members', needs: 'ViewMembers', hidden: true, under: 'analytics-server' },
  // The Discord page's Roles and Channels tabs (Discord tidy-up design, 2026-10-02): every role
  // with what is worth a look about it, and the channels nobody has written in for longest. Read
  // only. Under See analytics, which the Discord page itself needs: both are counts and dates about
  // the server and name nobody.
  { id: 'discord-roles', label: 'Discord roles', needs: 'ViewAnalytics', hidden: true, under: 'analytics-server', words: ['unused roles', 'tidy up'] },
  { id: 'discord-channels', label: 'Discord channels', needs: 'ViewAnalytics', hidden: true, under: 'analytics-server', words: ['quiet channels', 'tidy up'] },
  // Everyone Modbot has a record of, not only the group's roster: the people it has seen in an
  // instance or read about in the audit log have a profile and a history too, and no list led to
  // them. "People" rather than "Users", which is the settings screen for Modbot's own accounts.
  // See members opens it too, for the Members view alone (below).
  { id: 'people', label: 'People', group: 'Community', needsAny: ['ViewProfile', 'ViewMembers'] },
  // Not a page since 2026-09-27: the member list became People narrowed to members, with its
  // columns and filters (`MEMBERS_PATH`). It keeps its name so the VRChat page's Members tab, `g m`
  // and the palette still go to it, and the sidebar lights People while it is open. `/members`
  // and its old filters still open it (`membersAddress`).
  { id: 'members', label: 'Members', needs: 'ViewMembers', hidden: true, under: 'people', words: ['roster', 'VRChat members'] },
  // The group's open instances right now and who is in each.
  { id: 'live', label: 'Live', group: 'Community', needs: 'ViewLiveInstances', words: ['voice', 'online', 'instances now'] },
  { id: 'bans', label: 'Bans', group: 'Community', needs: 'ViewAuditLog', words: ['banned', 'ban list', 'unban', 'banned users'] },
  // What AI moderation rules flagged. A flag is a note about a person, so it needs ViewProfile;
  // dismissing one needs ReviewTickets, which the page checks for itself.
  { id: 'flags', label: 'Flags', group: 'Community', needs: 'ViewProfile' },
  // Reviews of a moderator's pattern (spec 5.8.5), beside Flags because both are things somebody
  // has to look at and decide. Gated on ReviewTickets because the people being reviewed should
  // not be closing them. It used to sit under a "Team" heading of its own, which named a different
  // thing from the Analytics page called Team.
  { id: 'reviews', label: 'Reviews', group: 'Community', needs: 'ReviewTickets' },
  { id: 'audit', label: 'Audit log', group: 'Community', needsAny: ['ViewAuditLog', 'ViewOperationalLog'], words: ['kick', 'warn', 'log', 'history'] },
  // Planned events, where each is published, and the calendar feed (calendar design).
  { id: 'calendar', label: 'Calendar', group: 'Community', needs: 'ViewCalendar', words: ['events', 'schedule'] },
  // Lists of worlds an event can pick its world from, each world with the players its game is for
  // (world lists design). Beside Calendar, whose events they are for, and under its permission.
  { id: 'world-lists', label: 'World lists', group: 'Community', needs: 'ViewCalendar', words: ['game night', 'worlds', 'shuffle', 'next game'] },
  // Posts sent to Discord and later other sites at a time, each with where it went and how
  // (posts design §4). After World lists and before Giveaways, beside the calendar it will post
  // for. Its four lists live at addresses of their own (`/marketing/sent`) so one can be linked
  // to; the Scheduled list is the page itself, the other three light Marketing.
  { id: 'marketing', label: 'Marketing', group: 'Community', needs: 'ViewPosts', words: ['posts', 'announcements', 'bluesky', 'social', 'schedule post'] },
  { id: 'marketing-sent', label: 'Sent posts', needs: 'ViewPosts', hidden: true, under: 'marketing' },
  { id: 'marketing-drafts', label: 'Draft posts', needs: 'ViewPosts', hidden: true, under: 'marketing' },
  { id: 'marketing-failed', label: 'Failed posts', needs: 'ViewPosts', hidden: true, under: 'marketing' },
  // Giveaways, their rules, who entered and how each draw went (giveaways design).
  { id: 'giveaways', label: 'Giveaways', group: 'Community', needs: 'ViewGiveaways' },
  // Saved lists of people, each a name and the giveaway rules, and who is in each now (lists
  // design). After Giveaways, whose rules they are. Needs See members and See profiles both: a
  // list's rules can ask about bans, flags and 18+ verification, so who is in "banned twice" is
  // moderation history, not only membership (decided 2026-10-01). Making one is the page's own check.
  { id: 'lists', label: 'Lists', group: 'Community', needsAll: ['ViewMembers', 'ViewProfile'], words: ['segments', 'regulars', 'export'] },
  // The "Integrations" heading itself (2026-09-30): a card for each outside service Modbot is
  // connected to, its status, and a Set up button into the part of Settings where it is set up.
  // Not a row of its own: the heading is its link (`heads`, `sidebarRows`), and on a phone its tile
  // sits before VRChat's. It needs what those Settings topics need. No `g` letter: none left fits.
  { id: 'integrations', label: 'Integrations', needs: 'ManageSettings', hidden: true, heads: 'Integrations', words: ['connections', 'email', 'set up'] },
  // The VRChat group and the Discord server, each as its own site shows it. Their charts are on
  // Stats; each keeps this week's numbers. The ids and addresses keep their old names, so links and
  // bookmarks still open the same pages. Under "Integrations" since 2026-09-27: they are the two
  // outside services Modbot is connected to, and the Stats page no longer sits among them.
  { id: 'analytics-group', label: 'VRChat', group: 'Integrations', needs: 'ViewAnalytics' },
  // Not in the page list since 2026-09-27: it is the Instances tab of the VRChat page, the way
  // vrchat.com shows a group's instances, so the sidebar lights VRChat while it is open. The
  // palette and `g i` still reach it, and its address is unchanged.
  { id: 'analytics-instances', label: 'Instances', needs: 'ViewAnalytics', hidden: true, under: 'analytics-group' },
  // The VRChat page's Posts and Settings tabs, at addresses of their own under the page's
  // (`/analytics/group/posts`) so a tab can be linked to. Reading posts is part of reading the
  // page; Settings is only for changing the group, so it needs the permission that does.
  { id: 'group-posts', label: 'VRChat posts', needs: 'ViewAnalytics', hidden: true, under: 'analytics-group' },
  { id: 'group-settings', label: 'VRChat settings', needs: 'EditGroupProfile', hidden: true, under: 'analytics-group' },
  // The Roles tab inside Settings, and the Gallery and Invites tabs. Looking at the gallery is part
  // of reading the page; the invites list and the roles are only for the people who manage them,
  // since VRChat itself shows them only to those.
  { id: 'group-roles', label: 'VRChat roles', needs: 'ManageGroupRoles', hidden: true, under: 'analytics-group' },
  { id: 'group-gallery', label: 'VRChat gallery', needs: 'ViewAnalytics', hidden: true, under: 'analytics-group' },
  { id: 'group-invites', label: 'VRChat invites', needs: 'ManageGroupInvites', hidden: true, under: 'analytics-group' },
  // The Discord server, beside the group: its own members, messages and voice (M5 spec §6).
  { id: 'analytics-server', label: 'Discord', group: 'Integrations', needs: 'ViewAnalytics' },
  // Not in the page list: the status rows at the foot of the sidebar say what it says, and each
  // one opens it at the part it names. The page, its address and every link to it are unchanged.
  // Called "Sync health" until 2026-09-26; it covers far more than syncing.
  { id: 'health', label: 'Health', needs: 'ViewOperationalLog', hidden: true },
  // Modbot's own program log. "Logs" alone collided with the person popup's tab and with the
  // audit log; the id and address stay `logs`.
  { id: 'logs', label: "Modbot's log", group: 'System', needs: 'ViewOperationalLog' },
  // Users and Roles are tabs inside Settings (the IAM tab), so somebody who may manage either but
  // not the settings themselves still needs the page to open. Which tabs they see is the page's
  // own check. Pair a companion opens it too, for the Paired companions topic: a moderator's own
  // companions are listed there.
  { id: 'settings', label: 'Settings', group: 'System', needsAny: ['ManageSettings', 'ManageUsers', 'ManageRoles', 'PairCompanion'] },
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
  // Stats opens on its first tab; Team and Worlds became its Moderation and Activity tabs on
  // 2026-09-27 (Stats page design).
  '/stats': '/stats/growth',
  '/analytics/team': '/stats/moderation',
  '/analytics/worlds': '/stats/activity',
  // Marketing opens on its first list (posts design §4.1).
  '/marketing': '/marketing/scheduled',
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
  'discord-roles': '',
  'discord-channels': '',
  people: 'n',
  live: 'l',
  calendar: 'e',
  'world-lists': 'x',
  marketing: 'q',
  'marketing-sent': '',
  'marketing-drafts': '',
  'marketing-failed': '',
  giveaways: 'p',
  lists: 'u',
  chat: 'c',
  bans: 'b',
  flags: 'f',
  audit: 'a',
  integrations: '',
  'analytics-group': 'g',
  'analytics-server': 'v',
  stats: 't',
  'stats-activity': 'w',
  'stats-moderation': '',
  'analytics-instances': 'i',
  'group-posts': '',
  'group-settings': '',
  'group-roles': '',
  'group-gallery': '',
  'group-invites': '',
  reviews: 'r',
  health: 'h',
  logs: 'o',
  settings: 's',
  account: 'y',
  cases: '',
  credits: '',
}

/**
 * The page whose name the title and the sidebar carry while `page` is open. A page opened from the
 * VRChat or Discord page's tab row (`groupTabFrom`, `serverTabFrom`) sits under that page's header,
 * so it carries that page's name: People opened as the VRChat page's Members tab said "People" under
 * a VRChat header (UX review 2026-09-27, idea 6). Opened from anywhere else it carries its own.
 */
export function shownAs(page: PageId, groupTab: PageId | null, serverTab: PageId | null): PageId {
  if (groupTab) return 'analytics-group'
  if (serverTab) return 'analytics-server'
  return page
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
 * sidebar, a page off it that is shown as part of one that is, and a page that is a heading
 * (Integrations). Other pages off the sidebar are reached from somewhere in particular -- a case
 * file from Bans -- and have no name to go to.
 */
export function goesByName(item: NavItem): boolean {
  return !('hidden' in item && item.hidden) || 'under' in item || 'heads' in item
}

/** The page a sidebar heading opens when it is clicked, if it opens one. */
export function headingPage(heading: string): NavItem | undefined {
  return NAV.find((n) => 'heads' in n && n.heads === heading)
}

/** One row of the sidebar: a heading, which may open a page of its own, or a page. */
export type SidebarRow =
  | { kind: 'heading'; label: string; page: PageId | null }
  | { kind: 'page'; item: NavItem }

/**
 * The sidebar for this person, in order: the pages of the page list, each heading before the first
 * of its pages. A heading travels with its first page this person is offered, so hiding Requests
 * does not take the Community heading away from People. A heading that opens a page of its own
 * (`heads`) is drawn for whoever may open that page, even with none of the pages under it.
 *
 * The phone's Menu grid reads the same rows: a heading that opens a page is a tile there, before
 * the tiles of the pages under it, and the other headings are left out.
 */
export function sidebarRows(me: CurrentUser): SidebarRow[] {
  const rows: SidebarRow[] = []
  let current: string | undefined
  let drawn = true

  for (const item of NAV) {
    if ('hidden' in item && item.hidden) continue

    const group = 'group' in item ? item.group : undefined
    if (group !== undefined && group !== current) {
      current = group
      const page = headingPage(group)
      drawn = false
      if (page && offered(me, page)) {
        rows.push({ kind: 'heading', label: group, page: page.id })
        drawn = true
      }
    }

    if (!offered(me, item)) continue
    if (!drawn && current !== undefined) {
      rows.push({ kind: 'heading', label: current, page: null })
      drawn = true
    }
    rows.push({ kind: 'page', item })
  }

  return rows
}

/** The other words a page answers to in the palette, besides its label. */
export function otherWords(item: NavItem): readonly string[] {
  return 'words' in item ? item.words : []
}

/**
 * How well something in the palette matches what was typed, lower first: 0 when its label is what
 * was typed, 1 when the label starts with it, 2 when every typed word is in the label, 3 when every
 * typed word is in the label or one of its other words. Null when it does not match. When nothing is
 * typed, everything matches.
 *
 * The order is what puts the group's Members above Discord members for "members", and Instances
 * above Live for "instances", without either page being a special case.
 */
export function matchRank(typed: string, label: string, other: readonly string[] = []): number | null {
  const words = typed.toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return 0

  const name = label.toLowerCase()
  const whole = words.join(' ')
  if (name === whole) return 0
  if (name.startsWith(whole)) return 1
  if (words.every((w) => name.includes(w))) return 2

  const rest = other.map((o) => o.toLowerCase())
  if (words.every((w) => name.includes(w) || rest.some((o) => o.includes(w)))) return 3
  return null
}

/**
 * The tiles of the phone's Menu sheet, in order: the sidebar's rows less their headings, except a
 * heading that is a page of its own (Integrations), which is a tile before the pages under it.
 */
export function menuPages(me: CurrentUser): NavItem[] {
  return sidebarRows(me).flatMap((row) => {
    if (row.kind === 'page') return [row.item]
    const page = row.page === null ? undefined : NAV.find((n) => n.id === row.page)
    return page ? [page] : []
  })
}

/**
 * The pages in the page list for this person, in order: the pages among the sidebar's rows and
 * the phone's Menu tiles (`sidebarRows`, which adds the headings), and the page the shell falls
 * back to. One list for all of them, so a page added or hidden here is added or hidden in each.
 */
export function listedPages(me: CurrentUser): NavItem[] {
  return NAV.filter((item) => !('hidden' in item && item.hidden) && offered(me, item))
}

/**
 * Whether a page is offered to this person: in the page list, in the palette and as a `g` chord.
 * A page they may open, unless it is switched off (`on` names the flag on the signed-in person
 * that says it is on): Chat with AI chat off would only say "Chat is off.". Every reader of the
 * page list asks this one question, so none of them has to know about the switch.
 *
 * Not `mayOpen`: a switched-off page still opens from a direct link and says so itself.
 */
export function offered(me: CurrentUser, item: NavItem): boolean {
  if (!mayOpen(me, item.id)) return false
  return 'on' in item ? Boolean(me[item.on]) : true
}

/** Whether this person may open a page. Pages with no requirement are open to everyone signed in. */
export function mayOpen(me: CurrentUser, id: PageId): boolean {
  const item = NAV.find((n) => n.id === id)
  if (!item) return true
  if ('needsAll' in item) return item.needsAll.every((p) => can(me, p))
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

/**
 * `(3) Modbot` while something is waiting, and the title as it was when nothing is. `more` when a
 * count in it came from a full page of a list with no total, the join requests: `(50+) Modbot`.
 */
export function titleWithCount(title: string, count: number, more = false): string {
  return count > 0 ? `(${count}${more ? '+' : ''}) ${title}` : title
}
