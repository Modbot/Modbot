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

export function canAny(user: CurrentUser | null, permissions: readonly string[]): boolean {
  return permissions.some((p) => can(user, p))
}
