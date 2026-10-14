import { useCallback, useEffect, useState } from 'react'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type NewStaffChecklistSettings } from '@/lib/api'
import { LongField, Outcome } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** The reminders are posted as cards, so the bot must see the channel, send there and embed links. */
const POST_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks']

const lines = (text: string) => text.split('\n').map((l) => l.trim()).filter((l) => l.length > 0)

/**
 * Settings → People and roles → New staff: the two lists a new staff member's checklist is made
 * from, one line each, and the channel the day-30 reminder and the 30-day check-in are posted in.
 * A change to the lines reaches checklists started after it.
 */
export function NewStaffChecklistCard() {
  const [data, setData] = useState<NewStaffChecklistSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .newStaffChecklistSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the checklist.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <PanelGrid className="grid-cols-12">
      {error || !data ? (
        <SettingsCard title="New staff checklist" span={12}>
          <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
            {error}
          </EmptyRow>
        </SettingsCard>
      ) : (
        <ChecklistForm settings={data} onSaved={setData} />
      )}
    </PanelGrid>
  )
}

function ChecklistForm({
  settings,
  onSaved,
}: {
  settings: NewStaffChecklistSettings
  onSaved: (next: NewStaffChecklistSettings) => void
}) {
  const [tasks, setTasks] = useState(settings.tasks.join('\n'))
  const [signOffs, setSignOffs] = useState(settings.signOffs.join('\n'))
  const [channelId, setChannelId] = useState(settings.reminderChannelId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setNewStaffChecklistSettings({ tasks: lines(tasks), signOffs: lines(signOffs), reminderChannelId: channelId || null })
      .then((next) => {
        onSaved(next)
        setTasks(next.tasks.join('\n'))
        setSignOffs(next.signOffs.join('\n'))
        setChannelId(next.reminderChannelId ?? '')
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="New staff checklist"
      span={12}
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save checklist'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-2xl flex-col gap-3">
        <LongField
          label="Tasks, one per line"
          placeholder=""
          rows={6}
          value={tasks}
          onChange={(v) => {
            setTasks(v)
            setSaved(false)
          }}
        />
        <LongField
          label="Buddy sign-off, one per line"
          placeholder=""
          rows={6}
          value={signOffs}
          onChange={(v) => {
            setSignOffs(v)
            setSaved(false)
          }}
        />
        <ChannelPicker
          label="Reminder channel"
          value={channelId}
          needs={POST_NEEDS}
          onChange={(id) => {
            setChannelId(id)
            setSaved(false)
          }}
        />
      </div>
    </SettingsCard>
  )
}
