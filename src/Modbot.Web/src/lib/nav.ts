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
  { id: 'bans', label: 'Bans', needs: 'ViewAuditLog' },
  { id: 'audit', label: 'Audit log', needsAny: ['ViewAuditLog', 'ViewOperationalLog'] },
  { id: 'metrics', label: 'Metrics', group: 'Insight', needs: 'ViewAnalytics' },
  { id: 'users', label: 'Users', group: 'Team', needs: 'ManageUsers' },
  { id: 'roles', label: 'Roles', needs: 'ManageRoles' },
  { id: 'health', label: 'Sync health', group: 'Setup', needs: 'ViewOperationalLog' },
  { id: 'settings', label: 'Settings', needs: 'ManageSettings' },
  { id: 'account', label: 'Your account', hidden: true },
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
