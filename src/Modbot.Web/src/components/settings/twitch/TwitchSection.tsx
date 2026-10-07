import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { SwitchBank } from '@/components/ui/switch-bank'
import { Textarea } from '@/components/ui/textarea'
import { api, ApiError, type TwitchPostPlaces, type TwitchSettings } from '@/lib/api'
import { useDiscordChannels } from '@/lib/discordLists'
import { timeOfDay } from '@/lib/format'
import { placesEqual, TWITCH_WORDS } from '@/lib/twitch'
import { Checkbox, ConfirmButton, Fact, Field, NumberField, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → Twitch (Twitch design): the Twitch app's client id and secret, the channel, Check, the
 * poll's switch (Live), and the "We're live on Twitch" post: its words, how long a stream must be
 * live and how long between two, and the sites it goes to.
 *
 * No words explain the setup (CLAUDE.md): the how-to is the docs page `integrations/twitch`, which
 * says how to make the free Twitch app. The secret is never shown back; only that one is saved.
 * Nothing is ticked to start.
 */
export function TwitchSection() {
  const [data, setData] = useState<TwitchSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .twitchSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load Twitch settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="twitch" title="Twitch">
      {error || !data ? (
        <Placeholder tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </Placeholder>
      ) : (
        <>
          <AppCard settings={data} onSaved={setData} />
          {(data.canGoLive || data.live) && <LiveCard settings={data} onSaved={setData} />}
          <PostCard settings={data} onSaved={setData} />
        </>
      )}
    </SettingsSection>
  )
}

function AppCard({ settings, onSaved }: { settings: TwitchSettings; onSaved: (next: TwitchSettings) => void }) {
  // Null until somebody types, so a field follows what is stored: after a save, which may have
  // read the channel out of a pasted address, and after Forget.
  const [typedId, setTypedId] = useState<string | null>(null)
  const [typedChannel, setTypedChannel] = useState<string | null>(null)
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState<'saving' | 'checking' | 'forgetting' | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const limitedUntil = useStillAhead(settings.limitedUntil)

  const clientId = typedId ?? settings.clientId ?? ''
  const channel = typedChannel ?? settings.channel ?? ''
  const changed =
    clientId.trim() !== (settings.clientId ?? '') ||
    channel.trim().toLowerCase() !== (settings.channel ?? '') ||
    secret.trim() !== ''

  const run = (kind: 'saving' | 'checking' | 'forgetting', call: () => Promise<TwitchSettings>, done?: string) => {
    setBusy(kind)
    setSaved(null)
    setProblem(null)

    call()
      .then((next) => {
        onSaved(next)
        if (kind !== 'checking') {
          setTypedId(null)
          setTypedChannel(null)
          setSecret('')
        }
        if (done) setSaved(done)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(null))
  }

  const save = () =>
    run(
      'saving',
      () =>
        api.setTwitchSettings({
          clientId: clientId.trim(),
          channel: channel.trim(),
          ...(secret.trim() ? { clientSecret: secret.trim() } : {}),
        }),
      'Saved.',
    )

  const check = () => run('checking', () => api.checkTwitch())
  const forget = () => run('forgetting', () => api.forgetTwitchSettings())

  const result = settings.check

  return (
    <SettingsCard
      title="App"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy !== null || !changed} onClick={save}>
            {busy === 'saving' ? 'Saving…' : 'Save'}
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={
              busy !== null || changed || !settings.clientId || !settings.secretStored || !settings.channel || limitedUntil !== null
            }
            onClick={check}
          >
            {busy === 'checking' ? 'Checking…' : 'Check'}
          </Button>
          {(settings.clientId || settings.secretStored || settings.channel) && (
            <ConfirmButton variant="outline" disabled={busy !== null} onConfirm={forget}>
              Forget
            </ConfirmButton>
          )}
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Fact label="Client secret" value={settings.secretStored ? 'Saved' : 'Not set'} />
      <Field label="Client id" mono value={clientId} maxLength={100} onChange={setTypedId} />
      <PasswordField label="Client secret" mono value={secret} onChange={setSecret} />
      <Field label="Channel" value={channel} maxLength={200} onChange={setTypedChannel} />

      {result && !result.problem && <Fact label="Channel" value={result.channelName ?? settings.channel ?? '—'} />}

      {limitedUntil ? (
        <Outcome tone="problem">Twitch is limiting Modbot. Try again after {timeOfDay(limitedUntil)}.</Outcome>
      ) : (
        <Outcome tone="problem">{result?.problem}</Outcome>
      )}
    </SettingsCard>
  )
}

/** The poll: the one switch that stops every call to Twitch for live status. */
function LiveCard({ settings, onSaved }: { settings: TwitchSettings; onSaved: (next: TwitchSettings) => void }) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const setLive = (live: boolean) => {
    setBusy(true)
    setProblem(null)

    api
      .setTwitchSettings({ live })
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard title="Live" footer={<Outcome tone="problem">{problem}</Outcome>}>
      <Switch checked={settings.live} disabled={busy || (!settings.live && !settings.canGoLive)} onChange={setLive}>
        Live
      </Switch>
      {settings.live && settings.polledAt && <Fact label="Last answer" value={timeOfDay(settings.polledAt)} />}
      <Outcome tone="problem">{settings.live ? settings.pollProblem : null}</Outcome>
    </SettingsCard>
  )
}

/**
 * The "We're live on Twitch" post: its title and text, with the stream's title, category and the
 * channel's link put in where the words {title}, {category} and {link} stand; how many minutes the
 * stream is live before it posts; how many hours between two; and the sites it goes to, all unticked
 * to start. The sending is the Marketing tab's, so the post shows there like any other.
 */
function PostCard({ settings, onSaved }: { settings: TwitchSettings; onSaved: (next: TwitchSettings) => void }) {
  const [title, setTitle] = useState(settings.postTitle)
  const [text, setText] = useState(settings.postText)
  const [minutes, setMinutes] = useState(String(settings.postAfterMinutes))
  const [hours, setHours] = useState(String(settings.postEveryHours))
  const [places, setPlaces] = useState<TwitchPostPlaces>(settings.places)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const channels = useDiscordChannels()

  const changed =
    title !== settings.postTitle ||
    text !== settings.postText ||
    minutes.trim() !== String(settings.postAfterMinutes) ||
    hours.trim() !== String(settings.postEveryHours) ||
    !placesEqual(places, settings.places)

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

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    api
      .setTwitchSettings({
        postTitle: title,
        postText: text,
        postAfterMinutes: Number(minutes),
        postEveryHours: Number(hours),
        places,
      })
      .then((next) => {
        onSaved(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Live post"
      span={12}
      footer={
        <>
          <Button type="button" size="xs" disabled={busy || !changed} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Title</span>
          <Input value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
        </label>
        <WordButtons onInsert={(word) => setTitle((current) => current + word)} />

        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Text</span>
          <Textarea rows={4} value={text} maxLength={2000} onChange={(e) => setText(e.target.value)} />
        </label>
        <WordButtons onInsert={(word) => setText((current) => current + word)} />

        <div className="grid gap-3 sm:grid-cols-2">
          <NumberField label="Minutes live" value={minutes} min={1} max={60} onChange={setMinutes} />
          <NumberField label="Hours between posts" value={hours} min={1} max={168} onChange={setHours} />
        </div>

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
              {places.vrChat.visibility === 'group' && settings.vrChatRoles.length > 0 && (
                <fieldset className="flex flex-col gap-1.5">
                  <legend className="mb-1 text-muted-foreground">Roles</legend>
                  <div className="grid grid-cols-1 gap-x-4 gap-y-1.5 sm:grid-cols-2">
                    {settings.vrChatRoles.map((role) => (
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
      </div>
    </SettingsCard>
  )
}

/** Buttons that put the stream's title, category or the channel's link into the words above. */
function WordButtons({ onInsert }: { onInsert: (word: string) => void }) {
  return (
    <div className="flex flex-wrap gap-2">
      {TWITCH_WORDS.map((w) => (
        <Button key={w.word} type="button" size="xs" variant="outline" onClick={() => onInsert(w.word)}>
          {w.label}
        </Button>
      ))}
    </div>
  )
}

/** The time Twitch limits Modbot until, while it is still ahead; null once it has passed. */
function useStillAhead(until: string | null): string | null {
  const [passed, setPassed] = useState<string | null>(null)

  useEffect(() => {
    if (!until) return
    const timer = window.setTimeout(() => setPassed(until), Math.max(0, new Date(until).getTime() - Date.now()))
    return () => window.clearTimeout(timer)
  }, [until])

  return until && passed !== until ? until : null
}
