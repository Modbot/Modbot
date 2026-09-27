import type { MissingGroupPermission } from '@/lib/api'
import { missingPermissionParts } from '@/lib/vrchatPermissions'

/**
 * VRChat refused because Modbot's own VRChat account lacks a group permission: which one, the
 * roles the account has, and the link to where roles are changed.
 */
export function VRChatPermissionMissing({
  missing,
  className,
}: {
  missing: MissingGroupPermission
  className?: string
}) {
  const parts = missingPermissionParts(missing)

  return (
    <p className={className}>
      Modbot's VRChat account needs {parts.permission ? <strong>{parts.permission}</strong> : 'a group permission'} in
      this group.{parts.roles && ` ${parts.roles}`}{' '}
      <a href={parts.link} target="_blank" rel="noreferrer" className="underline underline-offset-2">
        Change it in VRChat → Group settings → Roles ↗
      </a>
    </p>
  )
}
