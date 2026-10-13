import { useCallback, useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, Plus } from 'lucide-react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import {
  api,
  ApiError,
  type DiscordChannelPermission,
  type DiscordTicketType,
  type DiscordTickets,
} from '@/lib/api'
import { ConfirmButton, Field, LongField, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** Five buttons fit in the one row of a panel. */
const MOST_TYPES = 5

/** A transcript is a message with a file on it. */
const TRANSCRIPT_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks', 'attachFiles']

/** The panel is one message with buttons. */
const PANEL_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages']

/**
 * Settings → Discord → Tickets: the kinds of ticket members can open, where a closed ticket's
 * transcript goes, and posting the panel with its buttons. Every part saves on its own, to its own
 * endpoint, so the card has no single Save.
 */
export function TicketsCard() {
  const [data, setData] = useState<DiscordTickets | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordTickets()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load ticket settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Tickets">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return (
    <SettingsCard title="Tickets">
      <div className="flex max-w-lg flex-col gap-5">
        <TypeList data={data} onChange={setData} />
        <TranscriptChannel data={data} onChange={setData} />
        <PanelForm />
      </div>
    </SettingsCard>
  )
}

/** Runs one call, keeping its busy flag and error for the part that made it. */
function useCall() {
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const run = (call: () => Promise<unknown>, fallback: string, after?: () => void) => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    call()
      .then(() => {
        setSaved(true)
        after?.()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : fallback))
      .finally(() => setBusy(false))
  }

  return { busy, saved, problem, run }
}

function TypeList({ data, onChange }: { data: DiscordTickets; onChange: (next: DiscordTickets) => void }) {
  const move = useCall()

  const swap = (index: number, by: -1 | 1) => {
    const ids = data.types.map((t) => t.id)
    const [id] = ids.splice(index, 1)
    ids.splice(index + by, 0, id)
    move.run(() => api.reorderDiscordTicketTypes(ids).then(onChange), 'Could not move.')
  }

  return (
    <div className="flex flex-col gap-3">
      <h3 className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        Ticket types
      </h3>

      {data.types.map((type, i) => (
        <TypeRow
          key={type.id}
          type={type}
          first={i === 0}
          last={i === data.types.length - 1}
          moving={move.busy}
          onMove={(by) => swap(i, by)}
          onChange={onChange}
        />
      ))}

      <Outcome tone="problem">{move.problem}</Outcome>

      {data.types.length < MOST_TYPES && <NewType onChange={onChange} />}
    </div>
  )
}

function TypeRow({
  type,
  first,
  last,
  moving,
  onMove,
  onChange,
}: {
  type: DiscordTicketType
  first: boolean
  last: boolean
  moving: boolean
  onMove: (by: -1 | 1) => void
  onChange: (next: DiscordTickets) => void
}) {
  const [name, setName] = useState(type.name)
  const [staffRoleId, setStaffRoleId] = useState(type.staffRoleId ?? '')
  const [openingMessage, setOpeningMessage] = useState(type.openingMessage)
  const [holdsPrivateImages, setHoldsPrivateImages] = useState(type.holdsPrivateImages)
  const { busy, saved, problem, run } = useCall()

  const save = () =>
    run(
      () =>
        api
          .updateDiscordTicketType(type.id, { name, staffRoleId, openingMessage, holdsPrivateImages })
          .then(onChange),
      'Could not save.',
    )

  const remove = () => run(() => api.deleteDiscordTicketType(type.id).then(onChange), 'Could not delete.')

  return (
    <div className="flex flex-col gap-3 rounded-sm border border-(length:--hairline) border-border p-3">
      <Field label="Button" value={name} maxLength={80} onChange={setName} />
      <RolePicker label="Staff role" value={staffRoleId} onChange={setStaffRoleId} needsMention />
      <LongField label="Opening message" value={openingMessage} placeholder="" rows={4} onChange={setOpeningMessage} />
      <Switch checked={holdsPrivateImages} onChange={setHoldsPrivateImages}>
        Private images
      </Switch>

      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" size="xs" onClick={save} disabled={busy}>
          {busy ? 'Saving…' : 'Save'}
        </Button>
        <Button type="button" variant="ghost" size="icon-sm" disabled={first || moving} onClick={() => onMove(-1)}>
          <ArrowUp className="size-4" />
          <span className="sr-only">Move up</span>
        </Button>
        <Button type="button" variant="ghost" size="icon-sm" disabled={last || moving} onClick={() => onMove(1)}>
          <ArrowDown className="size-4" />
          <span className="sr-only">Move down</span>
        </Button>
        <ConfirmButton onConfirm={remove} disabled={busy}>
          Delete
        </ConfirmButton>
        <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}

function NewType({ onChange }: { onChange: (next: DiscordTickets) => void }) {
  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [staffRoleId, setStaffRoleId] = useState('')
  const [openingMessage, setOpeningMessage] = useState('')
  const [holdsPrivateImages, setHoldsPrivateImages] = useState(false)
  const { busy, problem, run } = useCall()

  if (!open)
    return (
      <div>
        <Button type="button" variant="outline" size="sm" onClick={() => setOpen(true)}>
          <Plus className="size-4" />
          Add ticket type
        </Button>
      </div>
    )

  const clear = () => {
    setName('')
    setStaffRoleId('')
    setOpeningMessage('')
    setHoldsPrivateImages(false)
    setOpen(false)
  }

  const add = () =>
    run(
      () =>
        api.addDiscordTicketType({ name, staffRoleId, openingMessage, holdsPrivateImages }).then((next) => {
          onChange(next)
          clear()
        }),
      'Could not add.',
    )

  return (
    <div className="flex flex-col gap-3 rounded-sm border border-(length:--hairline) border-border p-3">
      <Field label="Button" value={name} maxLength={80} onChange={setName} />
      <RolePicker label="Staff role" value={staffRoleId} onChange={setStaffRoleId} needsMention />
      <LongField label="Opening message" value={openingMessage} placeholder="" rows={4} onChange={setOpeningMessage} />
      <Switch checked={holdsPrivateImages} onChange={setHoldsPrivateImages}>
        Private images
      </Switch>

      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" size="xs" onClick={add} disabled={busy}>
          {busy ? 'Adding…' : 'Add'}
        </Button>
        <Button type="button" size="xs" variant="ghost" onClick={clear} disabled={busy}>
          Cancel
        </Button>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}

function TranscriptChannel({ data, onChange }: { data: DiscordTickets; onChange: (next: DiscordTickets) => void }) {
  const [channelId, setChannelId] = useState(data.transcriptChannelId ?? '')
  const { busy, saved, problem, run } = useCall()

  return (
    <div className="flex flex-col gap-3">
      <ChannelPicker label="Transcript channel" value={channelId} onChange={setChannelId} needs={TRANSCRIPT_NEEDS} />
      <div className="flex flex-wrap items-center gap-2">
        <Button
          type="button"
          size="xs"
          disabled={busy}
          onClick={() =>
            run(() => api.setDiscordTicketTranscriptChannel(channelId).then(onChange), 'Could not save.')
          }
        >
          {busy ? 'Saving…' : 'Save transcript channel'}
        </Button>
        <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}

function PanelForm() {
  const [channelId, setChannelId] = useState('')
  const [text, setText] = useState('')
  const { busy, saved, problem, run } = useCall()

  return (
    <div className="flex flex-col gap-3">
      <h3 className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        Ticket panel
      </h3>
      <ChannelPicker label="Channel" value={channelId} onChange={setChannelId} needs={PANEL_NEEDS} allowNone={false} />
      <LongField label="Text" value={text} placeholder="" rows={3} onChange={setText} />
      <div className="flex flex-wrap items-center gap-2">
        <Button
          type="button"
          size="xs"
          disabled={busy}
          onClick={() => run(() => api.postDiscordTicketPanel({ channelId, text }), 'Could not post.')}
        >
          {busy ? 'Posting…' : 'Post panel'}
        </Button>
        <Outcome tone="ok">{saved && 'Posted.'}</Outcome>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}
