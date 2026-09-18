// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and who may open which page is
// worth a test. `permissions.ts` imports nothing at run time, so it loads as it is too.
import type { CurrentUser } from './api.ts'
import { can, canAny } from './permissions.ts'

// No counts beside the labels yet. The prototype shows "14,208" next to Members, and it will
// again -- but a hardcoded number in a running deployment is indistinguishable from a real one,
// and a moderator has no way to tell they are looking at a screenshot. Counts return with the
// member sync that produces them (M1).
//
// Each entry names the permission it needs. The sidebar hides what the person cannot open; the
// server refuses the data regardless (accounts and access design §8). Permission names, never
// bits -- see lib/permissions.ts for why.
export const NAV = [
  { id: 'members', label: 'Members', needs: 'ViewMembers' },
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
  // feature (spec 10.3) and has no entry until it exists.
  { id: 'analytics-group', label: 'My Group', group: 'Analytics', needs: 'ViewAnalytics' },
  // The Discord server, beside the group: its own members, messages and voice (M5 spec §6).
  { id: 'analytics-server', label: 'My Server', needs: 'ViewAnalytics' },
  { id: 'analytics-team', label: 'My Team', needs: 'ViewAnalytics' },
  { id: 'analytics-worlds', label: 'Worlds', needs: 'ViewAnalytics' },
  { id: 'analytics-instances', label: 'Instances', needs: 'ViewAnalytics' },
  // Reviews of a moderator's pattern (spec 5.8.5). Under Team because they are about the team,
  // and gated on ReviewTickets because the people being reviewed should not be closing them.
  { id: 'reviews', label: 'Reviews', group: 'Team', needs: 'ReviewTickets' },
  { id: 'users', label: 'Users', group: 'Team', needs: 'ManageUsers' },
  { id: 'roles', label: 'Roles', needs: 'ManageRoles' },
  // Not in the page list: the status rows at the foot of the sidebar say what it says, and each
  // one opens it at the part it names. The page, its address and every link to it are unchanged.
  { id: 'health', label: 'Sync health', needs: 'ViewOperationalLog', hidden: true },
  { id: 'logs', label: 'Logs', group: 'Setup', needs: 'ViewOperationalLog' },
  { id: 'settings', label: 'Settings', group: 'Setup', needs: 'ManageSettings' },
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

/**
 * Addresses that moved. The old one still opens the page -- the footer and the Deployment card
 * pointed at `/credits` for months and so does anything anyone bookmarked -- and the URL is
 * quietly replaced with the new one.
 */
export const MOVED: Record<string, string> = { '/credits': CREDITS_PATH }

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
  users: 'u',
  roles: 'k',
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
