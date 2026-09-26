// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and who may open which page is
// worth a test. `permissions.ts` imports nothing at run time, so it loads as it is too.
import type { CurrentUser } from './api.ts'
import { can, canAny } from './permissions.ts'

// A count beside a label is only ever one read from the server: open reviews and open flags
// (App.tsx). The prototype showed a hardcoded "14,208" next to Members, and a made-up number in a
// running deployment is indistinguishable from a real one.
//
// Each entry names the permission it needs. The sidebar hides what the person cannot open; the
// server refuses the data regardless (accounts and access design §8). Permission names, never
// bits -- see lib/permissions.ts for why.
export const NAV = [
  { id: 'members', label: 'Members', needs: 'ViewMembers' },
  // The people asking to be let in, read from VRChat when the page is opened. Beside Members
  // because it is the same roster one step earlier.
  { id: 'requests', label: 'Requests', needs: 'ViewJoinRequests' },
  // The Discord server's own member list. Secondary to Members: separate, because most people are
  // on one side only and most never link.
  { id: 'discord-members', label: 'Discord members', needs: 'ViewMembers' },
  // Everyone Modbot has a record of, not only the group's roster: the people it has seen in an
  // instance or read about in the audit log have a profile and a history too, and no list led to
  // them. "People" rather than "Users", which is the settings screen for Modbot's own accounts.
  { id: 'people', label: 'People', needs: 'ViewProfile' },
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
  { id: 'audit', label: 'Audit log', needsAny: ['ViewAuditLog', 'ViewOperationalLog'] },
  // One page per question (spec 10.1), not one "metrics" page. Tracked Groups is a later
  // feature (spec 10.3) and has no entry until it exists. Named by what each is about: the team,
  // then each platform. Worlds and Instances are VRChat's, so they sit indented under it; the ids
  // and addresses keep their old names, so links and bookmarks still open the same pages.
  { id: 'analytics-team', label: 'Team', group: 'Analytics', needs: 'ViewAnalytics' },
  { id: 'analytics-group', label: 'VRChat', needs: 'ViewAnalytics' },
  { id: 'analytics-worlds', label: 'Worlds', indent: true, needs: 'ViewAnalytics' },
  { id: 'analytics-instances', label: 'Instances', indent: true, needs: 'ViewAnalytics' },
  // The Discord server, beside the group: its own members, messages and voice (M5 spec §6).
  { id: 'analytics-server', label: 'Discord', needs: 'ViewAnalytics' },
  // Reviews of a moderator's pattern (spec 5.8.5). Under Team because they are about the team,
  // and gated on ReviewTickets because the people being reviewed should not be closing them.
  { id: 'reviews', label: 'Reviews', group: 'Team', needs: 'ReviewTickets' },
  // Not in the page list: the status rows at the foot of the sidebar say what it says, and each
  // one opens it at the part it names. The page, its address and every link to it are unchanged.
  { id: 'health', label: 'Sync health', needs: 'ViewOperationalLog', hidden: true },
  { id: 'logs', label: 'Logs', group: 'System', needs: 'ViewOperationalLog' },
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
  reviews: 'r',
  health: 'h',
  logs: 'o',
  settings: 's',
  account: 'y',
  cases: '',
  credits: '',
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

/** `(3) Modbot` while something is waiting, and the title as it was when nothing is. */
export function titleWithCount(title: string, count: number): string {
  return count > 0 ? `(${count}) ${title}` : title
}
