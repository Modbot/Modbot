import type { Dispatch, SetStateAction } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { SwitchBank } from '@/components/ui/switch-bank'
import type { TwitchPostPlaces } from '@/lib/api'
import { useDiscordChannels } from '@/lib/discordLists'
import { Checkbox } from './fields'

/**
 * "Where it goes": the sites an automatic post is sent to, each unticked to start. Discord (channel,
 * role to mention, publish), VRChat (who sees it, which roles, notify) and Bluesky. Used by the
 * Twitch live post and the weekly spotlight, whose posts are sent from the Marketing tab.
 */
export function PostPlacesPicker({
  places,
  setPlaces,
  vrChatRoles,
}: {
  places: TwitchPostPlaces
  setPlaces: Dispatch<SetStateAction<TwitchPostPlaces>>
  vrChatRoles: { id: string; name: string }[]
}) {
  const channels = useDiscordChannels()

  const setDiscord = (change: Partial<NonNullable<TwitchPostPlaces['discord']>>) =>
    setPlaces((current) => ({
      ...current,
      discord: { channelId: null, roleId: null, publish: false, ...current.discord, ...change },
    }))

  const setVRChat = (change: Partial<NonNullable<TwitchPostPlaces['vrChat']>>) =>
    setPlaces((current) => ({
      ...current,
      vrChat: { visibility: 'group', roleIds: null, notify: false, ...current.vrChat, ...change },
    }))

  const toggleRole = (id: string, on: boolean) => {
    const held = places.vrChat?.roleIds ?? []
    setVRChat({ roleIds: on ? [...held.filter((r) => r !== id), id] : held.filter((r) => r !== id) })
  }

  const announcement =
    channels.data?.channels.find((c) => c.id === places.discord?.channelId)?.type === 'announcement'

  return (
    <fieldset className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
      <legend className="mb-1 text-muted-foreground">Where it goes</legend>

      <Checkbox
        checked={!!places.discord}
        onChange={(on) =>
          setPlaces((current) => ({ ...current, discord: on ? { channelId: null, roleId: null, publish: false } : null }))
        }
      >
        Discord
      </Checkbox>
      {places.discord && (
        <div className="flex flex-col gap-3 border-l-(length:--hairline) border-border pl-4">
          <ChannelPicker
            label="Channel"
            value={places.discord.channelId ?? ''}
            onChange={(id) => setDiscord({ channelId: id || null })}
            needs={['viewChannel', 'sendMessages', 'readMessageHistory']}
            allowNone={false}
          />
          <RolePicker
            label="Mention role"
            value={places.discord.roleId ?? ''}
            onChange={(id) => setDiscord({ roleId: id || null })}
            needsMention
          />
          {announcement && (
            <Checkbox checked={places.discord.publish} onChange={(publish) => setDiscord({ publish })}>
              Publish to followers
            </Checkbox>
          )}
        </div>
      )}

      <Checkbox
        checked={!!places.vrChat}
        onChange={(on) =>
          setPlaces((current) => ({ ...current, vrChat: on ? { visibility: 'group', roleIds: null, notify: false } : null }))
        }
      >
        VRChat
      </Checkbox>
      {places.vrChat && (
        <div className="flex flex-col gap-3 border-l-(length:--hairline) border-border pl-4">
          <div className="flex flex-col gap-1">
            <span className="text-muted-foreground">Who sees it</span>
            <SwitchBank
              value={places.vrChat.visibility}
              onChange={(visibility) => setVRChat({ visibility })}
              label="Who sees it"
              size="sm"
              options={[
                { value: 'group', label: 'Group' },
                { value: 'public', label: 'Everyone' },
              ]}
            />
          </div>
          {places.vrChat.visibility === 'group' && vrChatRoles.length > 0 && (
            <fieldset className="flex flex-col gap-1.5">
              <legend className="mb-1 text-muted-foreground">Roles</legend>
              <div className="grid grid-cols-1 gap-x-4 gap-y-1.5 sm:grid-cols-2">
                {vrChatRoles.map((role) => (
                  <Checkbox
                    key={role.id}
                    checked={(places.vrChat?.roleIds ?? []).includes(role.id)}
                    onChange={(on) => toggleRole(role.id, on)}
                  >
                    {role.name}
                  </Checkbox>
                ))}
              </div>
            </fieldset>
          )}
          <Checkbox checked={places.vrChat.notify} onChange={(notify) => setVRChat({ notify })}>
            Notify members
          </Checkbox>
        </div>
      )}

      <Checkbox checked={places.bluesky} onChange={(bluesky) => setPlaces((current) => ({ ...current, bluesky }))}>
        Bluesky
      </Checkbox>
    </fieldset>
  )
}
