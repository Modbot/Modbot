import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { useDiscordChannels, useDiscordRoles } from '@/lib/discordLists'
import type { RuleScope } from '@/lib/autoMod'
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
 */
export function RuleScopeFields({
  value,
  onChange,
}: {
  value: RuleScope
  onChange: (next: RuleScope) => void
}) {
  const { data: channels } = useDiscordChannels()
  const { data: roles } = useDiscordRoles()

  const channelName = (id: string) => {
    const channel = channels?.channels.find((c) => c.id === id)
    return channel ? `#${channel.name}` : id
  }

  const roleName = (id: string) => roles?.roles.find((r) => r.id === id)?.name ?? id

  const add = (key: 'channels' | 'exemptRoles', id: string) => {
    if (!id || value[key].includes(id)) return
    onChange({ ...value, [key]: [...value[key], id] })
  }

  const remove = (key: 'channels' | 'exemptRoles', id: string) =>
    onChange({ ...value, [key]: value[key].filter((i) => i !== id) })

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
        <RolePicker label="Add a role" value="" allowNone={false} onChange={(id) => add('exemptRoles', id)} />
        <Checkbox
          checked={value.exemptRolesSkipFlag}
          disabled={value.exemptRoles.length === 0}
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
