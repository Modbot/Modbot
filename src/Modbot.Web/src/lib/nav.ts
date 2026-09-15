import type { CurrentUser } from '@/lib/api'
import { can, canAny } from '@/lib/permissions'

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
  // The group's open instances right now and who is in each.
  { id: 'live', label: 'Live', needs: 'ViewLiveRooms' },
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
  { id: 'health', label: 'Sync health', group: 'Setup', needs: 'ViewOperationalLog' },
  { id: 'settings', label: 'Settings', needs: 'ManageSettings' },
  { id: 'account', label: 'Your account', hidden: true },
  // Reached from the Bans page and the subject pane, not from the sidebar. The server gates
  // reads on ViewProfile and writes on Ban; the page shows the refusal in words.
  { id: 'cases', label: 'Case files', hidden: true },
  // Reached from the footer and from Settings. No requirement: everyone signed in may read it.
  { id: 'credits', label: 'Credits', hidden: true },
] as const

export type NavItem = (typeof NAV)[number]

export type PageId = NavItem['id']

/** Whether this person may open a page. Pages with no requirement are open to everyone signed in. */
export function mayOpen(me: CurrentUser, id: PageId): boolean {
  const item = NAV.find((n) => n.id === id)
  if (!item) return true
  if ('needsAny' in item) return canAny(me, item.needsAny)
  if ('needs' in item) return can(me, item.needs)
  return true
}
