import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { dateTime } from '@/components/charts/format'
import {
  api,
  ApiError,
  type DiscordChannelPermission,
  type DiscordGateMode,
  type DiscordGateSettings,
} from '@/lib/api'
import { lengthOfTime } from '@/lib/format'
import { Fact, LongField, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** The gate message is posted, then fetched by id and rewritten when its words change. */
const GATE_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks', 'readMessageHistory']

const MODES: { value: DiscordGateMode; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'watch', label: 'Watch only' },
  { value: 'on', label: 'On' },
]

/**
 * Settings → Discord → Join gate (join gate design §3): whether the gate is off, watching or on,
 * the role it gives, the channel and message everybody sees first, the steps, how long somebody
 * may take before they are removed, and what happens on a join spike.
 *
 * Its own card with its own Save, because it saves to its own endpoint, like Account linking.
 */
export function JoinGateCard() {
  const [data, setData] = useState<DiscordGateSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordGateSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the join gate settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Join gate">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <JoinGateForm settings={data} onSaved={setData} />
}

function JoinGateForm({
  settings,
  onSaved,
}: {
  settings: DiscordGateSettings
  onSaved: (next: DiscordGateSettings) => void
}) {
  const [mode, setMode] = useState<DiscordGateMode>(settings.mode)
  const [memberRoleId, setMemberRoleId] = useState(settings.memberRoleId ?? '')
  const [channelId, setChannelId] = useState(settings.channelId ?? '')
  const [message, setMessage] = useState(settings.message ?? '')
  const [needsLink, setNeedsLink] = useState(settings.needsLink)
  const [needsEighteenPlus, setNeedsEighteenPlus] = useState(settings.needsEighteenPlus)
  const [removeAfter, setRemoveAfter] = useState<number | null>(settings.removeAfterMinutes)
  const [holdOnSpike, setHoldOnSpike] = useState(settings.holdOnSpike)
  const [pauseInvites, setPauseInvites] = useState(settings.pauseInvites)

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordGateSettings({
        mode,
        memberRoleId,
        channelId,
        message,
        needsLink,
        needsEighteenPlus: needsLink && needsEighteenPlus,
        removeAfterMinutes: removeAfter,
        holdOnSpike,
        pauseInvites,
      })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Join gate"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save join gate'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <Fact
          label="New joiners"
          value={settings.heldSince ? `Held since ${dateTime(settings.heldSince)}` : 'Not held'}
        />
      </div>

      <div className="flex max-w-lg flex-col gap-3">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Join gate</span>
          <Select value={mode} onChange={(v) => setMode(v as DiscordGateMode)} aria-label="Join gate">
            {MODES.map((m) => (
              <option key={m.value} value={m.value}>
                {m.label}
              </option>
            ))}
          </Select>
        </label>
        <RolePicker label="Member role" value={memberRoleId} onChange={setMemberRoleId} needsAssign />
        <ChannelPicker label="Gate channel" value={channelId} onChange={setChannelId} needs={GATE_NEEDS} />
        <LongField label="Message" value={message} onChange={setMessage} placeholder="" rows={4} />
      </div>

      <div className="flex max-w-lg flex-col gap-3">
        <Switch checked disabled onChange={() => undefined}>
          I agree
        </Switch>
        <Switch checked={needsLink} disabled={!settings.linkingReady && !needsLink} onChange={setNeedsLink}>
          Link VRChat account
        </Switch>
        <Switch checked={needsLink && needsEighteenPlus} disabled={!needsLink} onChange={setNeedsEighteenPlus}>
          18+ on VRChat
        </Switch>
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Remove after</span>
          <Select
            value={removeAfter === null ? 'never' : String(removeAfter)}
            onChange={(v) => setRemoveAfter(v === 'never' ? null : Number(v))}
            aria-label="Remove after"
          >
            <option value="never">Never</option>
            {settings.removeChoices.map((m) => (
              <option key={m} value={m}>
                {lengthOfTime(m)}
              </option>
            ))}
          </Select>
        </label>
        <Switch checked={holdOnSpike} onChange={setHoldOnSpike}>
          Hold new joiners on a join spike
        </Switch>
        <Switch checked={pauseInvites} onChange={setPauseInvites}>
          Allow pausing invites
        </Switch>
      </div>
    </SettingsCard>
  )
}
