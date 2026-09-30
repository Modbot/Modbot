import type { CurrentUser } from '@/lib/api'

/**
 * Whether the signed-in person may do something, by permission name.
 *
 * Names rather than bits, because the server's bitfield is 64 bits wide and Administrator is bit
 * 62 — a JavaScript number rounds that away the moment any other bit is set. Administrator
 * satisfies everything, the same way it does on the server.
 *
 * This decides what to show. The server decides what is allowed, on every request.
 */
export function can(user: CurrentUser | null, permission: string): boolean {
  if (!user) return false
  return user.permissionNames.includes('Administrator') || user.permissionNames.includes(permission)
}

/**
 * Whether something at this rank sits below the signed-in person's highest role. Ranks are role
 * positions, first at 0, so below means a bigger number; null holds no role and is below every
 * role. Administrator is above every rank.
 *
 * Manage users and Manage roles only reach what is below the caller, and the same rank is
 * refused. This decides what to grey; the server refuses on every request.
 */
/**
 * The position the server gives the Administrator role, and any role that carries the
 * Administrator permission: always first, above every number a role can be moved to.
 */
export const FIRST_POSITION = -2147483648

export function isBelowMe(me: CurrentUser, rank: number | null): boolean {
  if (me.permissionNames.includes('Administrator')) return true
  return (rank ?? Infinity) > (me.rank ?? Infinity)
}

export function canAny(user: CurrentUser | null, permissions: readonly string[]): boolean {
  return permissions.some((p) => can(user, p))
}
