// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
import type { GroupRoleBody, GroupRoleRow } from './api.ts'
import { VRCHAT_PERMISSIONS, vrchatPermissionLabel } from './vrchatPermissions.ts'

/**
 * The pieces of the Roles tab worth a test: the form's draft, what a Save sends, and which
 * permissions the form offers.
 */

/** A role as the form holds it while somebody edits it. */
export type RoleDraft = {
  name: string
  description: string
  /** VRChat's ids. */
  permissions: string[]
}

export function emptyRoleDraft(): RoleDraft {
  return { name: '', description: '', permissions: [] }
}

export function roleDraftOf(role: GroupRoleRow): RoleDraft {
  return { name: role.name ?? '', description: role.description ?? '', permissions: [...role.permissions] }
}

/** What is wrong with the draft, or null. VRChat documents only that a role needs a name. */
export function roleProblem(draft: RoleDraft): string | null {
  return draft.name.trim() ? null : 'A role needs a name.'
}

function sameSet(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false
  const set = new Set(a)
  return b.every((x) => set.has(x))
}

/**
 * What a Save sends: every field for a new role; for a change, only the fields that differ from the
 * role as the list showed it, and null when none do.
 */
export function roleBody(draft: RoleDraft, role: GroupRoleRow | null): GroupRoleBody | null {
  const name = draft.name.trim()
  const description = draft.description.trim()
  const permissions = [...new Set(draft.permissions)]

  if (!role) return { name, description, permissions }

  const body: GroupRoleBody = { id: role.id }
  if (name !== (role.name ?? '')) body.name = name
  if (description !== (role.description ?? '')) body.description = description
  if (!sameSet(permissions, role.permissions)) body.permissions = permissions

  return Object.keys(body).length > 1 ? body : null
}

/**
 * The permissions the form offers, with their labels: VRChat's list in the order its role editor
 * shows it, then any the role already holds that this build has no label for, so saving the role
 * keeps them. "Every permission" (`*`) is offered only on a role that has it, as VRChat's editor
 * does not offer it at all.
 */
export function permissionChoices(held: readonly string[]): { id: string; label: string }[] {
  const known = Object.keys(VRCHAT_PERMISSIONS).filter((id) => id !== '*' || held.includes('*'))
  const unknown = held.filter((id) => !(id in VRCHAT_PERMISSIONS))

  return [...known, ...unknown].map((id) => ({ id, label: vrchatPermissionLabel(id) }))
}

/** "3 permissions", "1 permission", "Every permission". */
export function permissionCount(permissions: readonly string[]): string {
  if (permissions.includes('*')) return VRCHAT_PERMISSIONS['*']
  return permissions.length === 1 ? '1 permission' : `${permissions.length} permissions`
}
