import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Picker } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { useDiscordChannels, useDiscordRoles } from '@/lib/discordLists'
import type { GroupRoleOption, RuleScope } from '@/lib/autoMod'
import { Checkbox } from '../fields'
import { Group } from './RuleFields'

const MODES: { value: RuleScope['channelMode']; label: string }[] = [
  { value: 'all', label: 'Every channel' },
  { value: 'only', label: 'Only these channels' },
  { value: 'except', label: 'All but these channels' },
]

/**
 * Where a rule runs and who it never acts on (AI moderation design §13.3).
 *
 * The pickers add one at a time and the chips remove, because the shared picker picks one thing
 * and a second picker would be a second way to do the same job.
 *
 * "Never act on" takes Discord roles, for a Discord message's author, and VRChat group roles, for a
 * VRChat profile's owner. A member of Modbot's own team is never acted on whatever is chosen here,
 * so there is nothing to choose for them.
 */
export function RuleScopeFields({
  value,
  groupRoles,
  onChange,
}: {
  value: RuleScope
  /** The managed group's roles, for the VRChat picker. Empty until the group is set and has been read once. */
  groupRoles: GroupRoleOption[]
  onChange: (next: RuleScope) => void
}) {
  const { data: channels } = useDiscordChannels()
  const { data: roles } = useDiscordRoles()

  const channelName = (id: string) => {
    const channel = channels?.channels.find((c) => c.id === id)
    return channel ? `#${channel.name}` : id
  }

  const roleName = (id: string) => roles?.roles.find((r) => r.id === id)?.name ?? id

  const groupRoleName = (id: string) => groupRoles.find((r) => r.id === id)?.name ?? id

  type IdList = 'channels' | 'exemptRoles' | 'exemptGroupRoles'

  const add = (key: IdList, id: string) => {
    if (!id || value[key].includes(id)) return
    onChange({ ...value, [key]: [...value[key], id] })
  }

  const remove = (key: IdList, id: string) =>
    onChange({ ...value, [key]: value[key].filter((i) => i !== id) })

  const groupRoleOptions = groupRoles
    .filter((r) => !value.exemptGroupRoles.includes(r.id))
    .map((r) => ({ id: r.id, label: r.name ?? r.id, keywords: r.id, marks: [] }))

  return (
    <>
      <Group label="Channels">
        <SwitchBank
          label="Channels"
          value={value.channelMode}
          options={MODES}
          onChange={(channelMode) => onChange({ ...value, channelMode })}
        />

        {value.channelMode !== 'all' && (
          <>
            <Chips ids={value.channels} name={channelName} onRemove={(id) => remove('channels', id)} />
            <ChannelPicker
              label="Add a channel"
              value=""
              allowNone={false}
              onChange={(id) => add('channels', id)}
            />
          </>
        )}
      </Group>

      <Group label="Never act on">
        <Chips ids={value.exemptRoles} name={roleName} onRemove={(id) => remove('exemptRoles', id)} />
        <RolePicker label="Add a Discord role" value="" allowNone={false} onChange={(id) => add('exemptRoles', id)} />
        <Chips
          ids={value.exemptGroupRoles}
          name={groupRoleName}
          onRemove={(id) => remove('exemptGroupRoles', id)}
        />
        <Picker
          label="Add a VRChat group role"
          value=""
          onChange={(id) => add('exemptGroupRoles', id)}
          groups={groupRoleOptions.length ? [{ heading: null, options: groupRoleOptions }] : []}
          current={null}
          allowNone={false}
          error={null}
        />
        <Checkbox
          checked={value.exemptRolesSkipFlag}
          disabled={value.exemptRoles.length === 0 && value.exemptGroupRoles.length === 0}
          onChange={(exemptRolesSkipFlag) => onChange({ ...value, exemptRolesSkipFlag })}
        >
          Do not flag them either
        </Checkbox>
      </Group>
    </>
  )
}

function Chips({
  ids,
  name,
  onRemove,
}: {
  ids: string[]
  name: (id: string) => string
  onRemove: (id: string) => void
}) {
  if (ids.length === 0) return null

  return (
    <div className="flex flex-wrap gap-1.5">
      {ids.map((id) => (
        <Badge key={id} variant="secondary" className="gap-1 pr-1">
          {name(id)}
          <Button
            size="xs"
            variant="ghost"
            className="h-4 w-4 p-0"
            aria-label={`Remove ${name(id)}`}
            onClick={() => onRemove(id)}
          >
            ×
          </Button>
        </Badge>
      ))}
    </div>
  )
}
