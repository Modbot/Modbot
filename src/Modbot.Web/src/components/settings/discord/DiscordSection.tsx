import { useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type OnboardingStatus } from '@/lib/api'
import { Fact, Field, LongField, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { ChannelsCard } from './ChannelsCard'
import { LinkingCard } from './LinkingCard'
import { SyncCard } from './SyncCard'

/**
 * Instance cards are posted, then fetched by id and rewritten -- and fetching a message needs
 * Read Message History, which posting alone does not.
 */
const ANNOUNCE_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks', 'readMessageHistory']

/**
 * Settings → Discord: the bot, the channels events are sent to, and instance announcements.
 *
 * Its own tab since events could go to many channels (Discord event routes design §7). Each card
 * saves on its own: the bot and announcement cards through the integrations save, which leaves
 * alone whatever a request does not name, and the channels and account linking cards through their
 * own endpoints.
 */
export function DiscordSection({
  status,
  refresh,
}: {
  status: OnboardingStatus | null
  refresh: () => Promise<void>
}) {
  return (
    <SettingsSection id="discord" title="Discord">
      {/* Mounted only once the status is in hand, so the forms start from the saved values. */}
      {status ? (
        <>
          <BotCard status={status} refresh={refresh} />
          <InstanceCard status={status} refresh={refresh} />
          <ChannelsCard />
          <LinkingCard />
          <SyncCard />
        </>
      ) : (
        <Placeholder>Loading…</Placeholder>
      )}
    </SettingsSection>
  )
}

/** A save through the integrations endpoint, with its outcome kept for the card's footer. */
function useSave(refresh: () => Promise<void>) {
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const run = (discord: NonNullable<Parameters<typeof api.saveIntegrations>[0]['discord']>, after?: () => void) => {
    setSaving(true)
    setSaved(false)
    setError(null)

    api
      .saveIntegrations({ discord })
      .then(async () => {
        setSaved(true)
        after?.()
        await refresh()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  return { saving, saved, error, run }
}

function BotCard({ status, refresh }: { status: OnboardingStatus; refresh: () => Promise<void> }) {
  const [botToken, setBotToken] = useState('')
  const [guildId, setGuildId] = useState(status.integrations.discordGuildId ?? '')
  const { saving, saved, error, run } = useSave(refresh)

  // A blank token field is left out rather than sent empty: empty clears the stored token, and
  // opening this card and pressing Save must not disconnect the bot.
  const save = (event: React.FormEvent) => {
    event.preventDefault()
    run({ ...(botToken ? { botToken } : {}), guildId }, () => setBotToken(''))
  }

  return (
    <SettingsCard
      title="Bot"
      footer={
        <>
          <Button type="submit" form="discord-bot" size="xs" disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <Fact label="Bot" value={status.integrations.discordConfigured ? 'Token stored' : 'Not configured'} />
      <form id="discord-bot" onSubmit={save} className="flex max-w-lg flex-col gap-3">
        <PasswordField label="Bot token" value={botToken} onChange={setBotToken} />
        <Field label="Guild id" value={guildId} onChange={setGuildId} placeholder="" />
      </form>
    </SettingsCard>
  )
}

function InstanceCard({ status, refresh }: { status: OnboardingStatus; refresh: () => Promise<void> }) {
  const [channelId, setChannelId] = useState(status.integrations.discordInstanceChannelId ?? '')
  const [message, setMessage] = useState(status.integrations.discordInstanceMessage ?? '')
  const [showNames, setShowNames] = useState(status.integrations.discordInstanceShowNames)
  const { saving, saved, error, run } = useSave(refresh)

  const save = (event: React.FormEvent) => {
    event.preventDefault()
    run({ instanceChannelId: channelId, instanceMessage: message, instanceShowNames: showNames })
  }

  return (
    <SettingsCard
      title="Instance announcements"
      footer={
        <>
          <Button type="submit" form="discord-instances" size="xs" disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <form id="discord-instances" onSubmit={save} className="flex max-w-lg flex-col gap-3">
        <ChannelPicker
          label="Announce open instances in this channel"
          value={channelId}
          onChange={setChannelId}
          needs={ANNOUNCE_NEEDS}
        />
        <LongField
          label="Message above each announcement"
          value={message}
          onChange={setMessage}
          placeholder="Come and join us!"
        />
        <Switch checked={showNames} onChange={setShowNames}>
          Show names
        </Switch>
      </form>
    </SettingsCard>
  )
}
