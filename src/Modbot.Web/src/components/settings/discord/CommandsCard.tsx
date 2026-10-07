import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordCommandSwitch, type DiscordCommandsSettings } from '@/lib/api'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/**
 * Settings → Discord → Commands (Discord commands design §3.8): one switch for each command and
 * right-click menu the bot has, labelled with its name only. A command that is off is not
 * registered on the server.
 *
 * Its own card with its own Save, because it saves to its own endpoint.
 */
export function CommandsCard() {
  const [data, setData] = useState<DiscordCommandsSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordCommandsSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load the command switches.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Commands">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <CommandsForm settings={data} onSaved={setData} />
}

/** What a command is called in Discord: a slash command starts with a slash, a menu is its own words. */
function nameOf(command: DiscordCommandSwitch) {
  return command.menu ? command.name : `/${command.name}`
}

function CommandsForm({
  settings,
  onSaved,
}: {
  settings: DiscordCommandsSettings
  onSaved: (next: DiscordCommandsSettings) => void
}) {
  const [on, setOn] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(settings.commands.map((c) => [c.name, c.on])),
  )

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordCommandsSettings(on)
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Commands"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save commands'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        {settings.commands.map((command) => (
          <Switch
            key={command.name}
            checked={on[command.name] ?? false}
            onChange={(next) => setOn({ ...on, [command.name]: next })}
          >
            {nameOf(command)}
          </Switch>
        ))}
      </div>
    </SettingsCard>
  )
}
