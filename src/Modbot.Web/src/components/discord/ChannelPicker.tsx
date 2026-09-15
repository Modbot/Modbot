import type { DiscordChannel, DiscordChannelPermission, DiscordChannelType } from '@/lib/api'
import { missingLabel, useDiscordChannels } from '@/lib/discordLists'
import { Picker, type PickerGroup, type PickerMark } from './Picker'

const POSTABLE: DiscordChannelType[] = ['text', 'announcement']

/**
 * Picks one channel in the bot's Discord server, grouped under its category and shown as `#name`.
 *
 * `needs` names the permissions the setting depends on -- posting needs View Channel, Send
 * Messages and Embed Links; reading history back needs View Channel and Read Message History.
 * A channel where the bot lacks any of them is still offered, marked with the missing ones by
 * name (M5 spec §7), because the fix is a Discord setting and the moderator may be about to make
 * it.
 *
 * A saved id that is not in the list stays selected and reads as an unknown channel; a deleted one
 * keeps its name and reads as removed. Neither is cleared on the moderator's behalf.
 */
export function ChannelPicker({
  label,
  value,
  onChange,
  needs = [],
  types = POSTABLE,
  allowNone = true,
  disabled,
}: {
  label: string
  /** The saved channel id, or an empty string for none. */
  value: string
  onChange: (channelId: string) => void
  /** Permissions the setting needs. Channels lacking any are marked with their names. */
  needs?: readonly DiscordChannelPermission[]
  /** Which kinds of channel may be picked. Text and announcement channels unless told otherwise. */
  types?: readonly DiscordChannelType[]
  /** Offer "None", which sets an empty string. */
  allowNone?: boolean
  disabled?: boolean
}) {
  const { data, error, reload } = useDiscordChannels()
  const channels = data?.channels ?? []

  const marksFor = (channel: DiscordChannel): PickerMark[] => {
    if (channel.removed) return [{ text: 'Removed', tone: 'quiet' }]
    const missing = missingLabel(channel, needs)
    return missing ? [{ text: missing, tone: 'problem' }] : []
  }

  const option = (channel: DiscordChannel) => ({
    id: channel.id,
    label: `#${channel.name}`,
    keywords: channel.id,
    marks: marksFor(channel),
  })

  const live = channels.filter((c) => !c.removed)
  const pickable = live.filter((c) => types.includes(c.type))

  // Discord's own layout: channels outside any category first, then each category in order.
  const categories = live
    .filter((c) => c.type === 'category')
    .sort((a, b) => a.position - b.position || a.id.localeCompare(b.id))

  const byPosition = (a: DiscordChannel, b: DiscordChannel) => a.position - b.position || a.id.localeCompare(b.id)
  const categoryIds = new Set(categories.map((c) => c.id))

  const groups: PickerGroup[] = [
    {
      heading: null,
      options: pickable
        .filter((c) => !c.categoryId || !categoryIds.has(c.categoryId))
        .sort(byPosition)
        .map(option),
    },
    ...categories.map((category) => ({
      heading: category.name,
      options: pickable
        .filter((c) => c.categoryId === category.id)
        .sort(byPosition)
        .map(option),
    })),
  ].filter((g) => g.options.length > 0)

  const saved = value ? channels.find((c) => c.id === value) : undefined
  const current = !value
    ? null
    : saved
      ? { label: `#${saved.name}`, marks: marksFor(saved) }
      : data
        ? { label: 'Unknown channel', marks: [{ text: value, tone: 'quiet' as const }] }
        : { label: value, marks: [] }

  return (
    <Picker
      label={label}
      value={value}
      onChange={onChange}
      groups={groups}
      current={current}
      allowNone={allowNone}
      error={error}
      disabled={disabled}
      onOpen={reload}
    />
  )
}
