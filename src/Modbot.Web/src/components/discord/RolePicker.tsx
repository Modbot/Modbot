import type { DiscordRole } from '@/lib/api'
import { useDiscordRoles } from '@/lib/discordLists'
import { Picker, type PickerMark } from './Picker'

/** A role's colour as `#rrggbb`, or null for a role with none. */
function colorOf(role: DiscordRole): string | null {
  return role.color ? `#${role.color.toString(16).padStart(6, '0')}` : null
}

/**
 * Picks one role in the bot's Discord server, highest first, with each role's colour.
 *
 * With `needsAssign`, roles the bot could not hand out -- it lacks Manage Roles, the role sits
 * above the bot's own, or the role belongs to a bot or an integration -- are marked. They stay
 * pickable, as channels missing a permission do, because the fix is in Discord.
 *
 * A saved id that is not in the list stays selected and reads as an unknown role.
 */
export function RolePicker({
  label,
  value,
  onChange,
  needsAssign = false,
  includeEveryone = false,
  allowNone = true,
  disabled,
}: {
  label: string
  /** The saved role id, or an empty string for none. */
  value: string
  onChange: (roleId: string) => void
  /** Mark roles the bot cannot give to somebody. */
  needsAssign?: boolean
  /** Offer @everyone. Left out unless asked for. */
  includeEveryone?: boolean
  /** Offer "None", which sets an empty string. */
  allowNone?: boolean
  disabled?: boolean
}) {
  const { data, error, reload } = useDiscordRoles()
  const roles = data?.roles ?? []

  const marksFor = (role: DiscordRole): PickerMark[] => {
    if (role.removed) return [{ text: 'Removed', tone: 'quiet' }]
    if (needsAssign && !role.botCanAssign) return [{ text: 'Bot cannot assign', tone: 'problem' }]
    return []
  }

  const options = roles
    .filter((r) => !r.removed && (includeEveryone || !r.everyone))
    .sort((a, b) => b.position - a.position || a.id.localeCompare(b.id))
    .map((role) => ({
      id: role.id,
      label: role.name,
      keywords: role.id,
      marks: marksFor(role),
      dot: colorOf(role),
    }))

  const saved = value ? roles.find((r) => r.id === value) : undefined
  const current = !value
    ? null
    : saved
      ? { label: saved.name, marks: marksFor(saved), dot: colorOf(saved) }
      : data
        ? { label: 'Unknown role', marks: [{ text: value, tone: 'quiet' as const }] }
        : { label: value, marks: [] }

  return (
    <Picker
      label={label}
      value={value}
      onChange={onChange}
      groups={options.length ? [{ heading: null, options }] : []}
      current={current}
      allowNone={allowNone}
      error={error}
      disabled={disabled}
      onOpen={reload}
    />
  )
}
