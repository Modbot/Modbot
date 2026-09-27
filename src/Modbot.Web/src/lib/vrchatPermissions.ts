// Relative and with the extension on anything imported at run time, so the Node test runner can load
// this file as it is. It imports nothing at run time.
import type { MissingGroupPermission } from './api.ts'

/**
 * VRChat's group permissions in the words its role editor uses (Group settings → Roles → a role's
 * page on vrchat.com, 2026-09-25). VRChat's API names them by id alone. `*` is the owner's "all
 * permissions", which the editor does not list. An id this build does not know is spelled out.
 */
export const VRCHAT_PERMISSIONS: Record<string, string> = {
  '*': 'Every permission',
  'group-members-manage': 'Manage Group Member Data',
  'group-data-manage': 'Manage Group Data',
  'group-audit-view': 'View Audit log',
  'group-roles-manage': 'Manage Group Roles',
  'group-default-role-manage': 'Manage Group Default Role',
  'group-roles-assign': 'Assign Group Roles',
  'group-bans-manage': 'Manage Group Bans',
  'group-members-remove': 'Remove Group Members',
  'group-members-viewall': 'View All Members',
  'group-announcement-manage': 'Manage Group Announcement',
  'group-instance-announcement-create': 'Create Instance Announcement',
  'group-calendar-manage': 'Manage Group Calendar',
  'group-instance-calendar-link': 'Link Instances and Events',
  'group-galleries-manage': 'Manage Group Galleries',
  'group-invites-manage': 'Manage Group Invites',
  'group-instance-moderate': 'Moderate Group Instances',
  'group-instance-manage': 'Manage Group Instances',
  'group-instance-queue-priority': 'Group Instance Queue Priority',
  'group-instance-age-gated-create': 'Create Age Gated Instances',
  'group-instance-public-create': 'Create Group Public Instances',
  'group-instance-plus-create': 'Create Group+ Instances',
  'group-instance-open-create': 'Create Members-Only Group Instances',
  'group-instance-restricted-create': 'Role-Restrict Members-Only Instances',
  'group-instance-plus-portal': 'Portal to Group+ Instances',
  'group-instance-plus-portal-unlocked': 'Unlocked Portal to Group+ Instances',
  'group-instance-join': 'Join Group Instances',
  'group-instance-bypass-avatar-performance': 'Bypass Avatar Performance Requirements',
}

/** VRChat's label for a group permission, or the id itself when this build does not know it. */
export function vrchatPermissionLabel(id: string): string {
  return VRCHAT_PERMISSIONS[id] ?? id
}

/** The page in VRChat where the group's roles, and so their permissions, are changed. */
export function vrchatRolesPage(groupId: string): string {
  return `https://vrchat.com/home/group/${encodeURIComponent(groupId)}/settings/roles`
}

/** What a missing-permission refusal says, in pieces the component lays out. */
export type MissingPermissionParts = {
  /** The permission's label, or null when Modbot does not know which one it is. */
  permission: string | null
  /** "It has the role …" / "It has the roles …" / "It has no roles.", or null when not known. */
  roles: string | null
  link: string
}

export function missingPermissionParts(missing: MissingGroupPermission): MissingPermissionParts {
  return {
    permission: missing.permission ? vrchatPermissionLabel(missing.permission) : null,
    roles: rolesSentence(missing.roles),
    link: vrchatRolesPage(missing.groupId),
  }
}

/** The whole message as one line of text, for places that show only text. */
export function missingPermissionText(missing: MissingGroupPermission): string {
  const parts = missingPermissionParts(missing)
  const what = parts.permission ?? 'a group permission'
  return [`Modbot's VRChat account needs ${what} in this group.`, parts.roles].filter(Boolean).join(' ')
}

function rolesSentence(roles: string[] | null): string | null {
  if (roles === null) return null
  if (roles.length === 0) return 'It has no roles.'

  const quoted = roles.map((r) => `"${r}"`)
  if (quoted.length === 1) return `It has the role ${quoted[0]}.`

  return `It has the roles ${quoted.slice(0, -1).join(', ')} and ${quoted[quoted.length - 1]}.`
}

/**
 * The missing permission a refusal's body carries, when it carries one: the join request list
 * answers with `{ error, missingGroupPermission }` when VRChat refused to read it.
 */
export function missingPermissionOf(detail: unknown): MissingGroupPermission | null {
  if (typeof detail !== 'object' || detail === null) return null

  const missing = (detail as { missingGroupPermission?: unknown }).missingGroupPermission
  if (typeof missing !== 'object' || missing === null) return null

  const m = missing as Partial<MissingGroupPermission>
  return typeof m.groupId === 'string' && m.groupId
    ? { permission: m.permission ?? null, groupId: m.groupId, roles: m.roles ?? null, said: m.said ?? null }
    : null
}
