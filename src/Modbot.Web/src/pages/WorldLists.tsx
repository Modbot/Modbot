import { useCallback, useEffect, useState } from 'react'
import { X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { WorldLink } from '@/components/facts'
import { Field, Outcome } from '@/components/settings/fields'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ApiError } from '@/lib/api'
import type { CalendarWorld } from '@/lib/calendar'
import { useShortcuts } from '@/lib/shortcuts'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { playersText, worldListApi, type WorldList, type WorldListWorld, type WorldLists as WorldListsView } from '@/lib/worldLists'
import { PageMessage } from '@/pages/analytics/shared'

/** How long typing rests before the known worlds are searched. */
const SEARCH_AFTER_MS = 250

/**
 * World lists (world lists design): lists of worlds kept in Modbot, each world with the players its
 * game is for, for an event to pick its world from.
 */
export function WorldLists() {
  const [data, setData] = useState<WorldListsView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<WorldList | 'new' | null>(null)
  const [deleting, setDeleting] = useState<WorldList | null>(null)

  useShortcuts(data?.canManage ? [{ label: 'New list', group: 'Page', page: true, run: () => setEditing('new') }] : [])

  const load = useCallback(() => {
    worldListApi
      .all()
      .then((answer) => {
        setData(answer)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the world lists.',
        ),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      {data.canManage && (
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex-1" />
          <Button onClick={() => setEditing('new')}>New list</Button>
        </div>
      )}

      {data.lists.length === 0 ? (
        <PageMessage>No world lists.</PageMessage>
      ) : (
        data.lists.map((list) => (
          <ListCard
            key={list.id}
            list={list}
            canManage={data.canManage}
            onEdit={() => setEditing(list)}
            onDelete={() => setDeleting(list)}
          />
        ))
      )}

      {editing && (
        <ListForm
          list={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            load()
          }}
        />
      )}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={`Delete the world list “${deleting?.name ?? ''}”?`}
        action="Delete"
        failed="Could not delete the list."
        onConfirm={() => (deleting ? worldListApi.remove(deleting.id) : Promise.resolve())}
        onDone={() => {
          setDeleting(null)
          load()
        }}
      />
    </div>
  )
}

/** One list: its name, the events that pick from it, and its worlds with their players. */
function ListCard({
  list,
  canManage,
  onEdit,
  onDelete,
}: {
  list: WorldList
  canManage: boolean
  onEdit: () => void
  onDelete: () => void
}) {
  const inUse = list.usedBy.some((u) => u.state === 'draft' || u.state === 'scheduled' || u.state === 'open')

  return (
    <Card className="flex flex-col">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-b-(length:--hairline) px-(--panel-pad) py-1.5">
        <span className="font-medium [overflow-wrap:anywhere]">{list.name}</span>
        <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {list.worlds.length} {list.worlds.length === 1 ? 'world' : 'worlds'}
        </span>
        <span className="flex flex-wrap gap-1">
          {list.usedBy.map((u) => (
            <Badge key={u.eventId} variant="secondary">
              {u.title}
            </Badge>
          ))}
        </span>
        <div className="flex-1" />
        {canManage && (
          <span className="flex gap-2">
            <Button size="sm" variant="outline" onClick={onEdit}>
              Edit
            </Button>
            <Button size="sm" variant="destructive" disabled={inUse} onClick={onDelete}>
              Delete
            </Button>
          </span>
        )}
      </div>

      {list.worlds.length === 0 ? (
        <div className="px-(--panel-pad) py-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          No worlds.
        </div>
      ) : (
        <ul className="divide-y-(--hairline) divide-border">
          {list.worlds.map((w) => (
            <li key={w.worldId} className="flex min-h-(--row-h) items-center gap-3 px-(--panel-pad) py-1">
              <WorldPicture url={w.thumbnailUrl} />
              <WorldLink id={w.worldId} name={w.name} unnamed="id" className="min-w-0 flex-1 [overflow-wrap:anywhere]" />
              <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {playersText(w.minPlayers, w.maxPlayers) ?? '—'}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

function WorldPicture({ url }: { url: string | null }) {
  return url ? (
    <img src={vrchatMedia(url)} alt="" className="aspect-[4/3] w-12 shrink-0 rounded-sm object-cover" loading="lazy" />
  ) : (
    <span aria-hidden className="aspect-[4/3] w-12 shrink-0 rounded-sm bg-muted" />
  )
}

/** A world on the form, with its players as typed. */
type Row = { worldId: string; name: string | null; thumbnailUrl: string | null; min: string; max: string }

function rowOf(w: WorldListWorld): Row {
  return {
    worldId: w.worldId,
    name: w.name,
    thumbnailUrl: w.thumbnailUrl,
    min: w.minPlayers === null ? '' : String(w.minPlayers),
    max: w.maxPlayers === null ? '' : String(w.maxPlayers),
  }
}

function players(text: string): number | null {
  const trimmed = text.trim()
  if (trimmed === '') return null
  const n = Number(trimmed)
  return Number.isFinite(n) ? Math.trunc(n) : null
}

/** The create and edit form: the name, the worlds and their players, and a way to add more. */
function ListForm({ list, onClose, onSaved }: { list: WorldList | null; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(list?.name ?? '')
  const [rows, setRows] = useState<Row[]>(() => (list?.worlds ?? []).map(rowOf))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const add = (world: CalendarWorld) =>
    setRows((current) =>
      current.some((r) => r.worldId === world.worldId)
        ? current
        : [...current, { worldId: world.worldId, name: world.name, thumbnailUrl: world.thumbnailUrl, min: '', max: '' }],
    )

  const change = (worldId: string, key: 'min' | 'max', value: string) =>
    setRows((current) => current.map((r) => (r.worldId === worldId ? { ...r, [key]: value } : r)))

  const save = () => {
    setBusy(true)
    setError(null)

    const body = {
      name,
      worlds: rows.map((r) => ({ worldId: r.worldId, minPlayers: players(r.min), maxPlayers: players(r.max) })),
    }
    const request = list ? worldListApi.update(list.id, body) : worldListApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the list.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={list ? 'Edit world list' : 'New world list'}
        className="max-w-[720px]"
        bodyClassName="max-h-[78vh] overflow-y-auto"
        foot={
          <DialogFoot>
            {error && <Outcome tone="problem">{error}</Outcome>}
            <Button size="sm" variant="outline" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button size="sm" onClick={save} disabled={busy}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
          <Field label="Name" value={name} maxLength={100} onChange={setName} />

          <div className="flex flex-col gap-2 border-t border-t-(length:--hairline) pt-3">
            <span className="font-label">Worlds</span>

            {rows.length > 0 && (
              <div className="flex flex-col gap-3 sm:grid sm:grid-cols-[1fr_5.5rem_5.5rem_auto] sm:items-center sm:gap-x-2 sm:gap-y-1.5">
                <div className="hidden sm:contents">
                  <span />
                  <span className="text-muted-foreground">Fewest players</span>
                  <span className="text-muted-foreground">Most players</span>
                  <span />
                </div>
                {rows.map((r) => (
                  <WorldRow
                    key={r.worldId}
                    row={r}
                    onChange={(key, value) => change(r.worldId, key, value)}
                    onRemove={() => setRows((current) => current.filter((x) => x.worldId !== r.worldId))}
                  />
                ))}
              </div>
            )}

            <AddWorld onAdd={add} />
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function WorldRow({
  row,
  onChange,
  onRemove,
}: {
  row: Row
  onChange: (key: 'min' | 'max', value: string) => void
  onRemove: () => void
}) {
  const label = row.name ?? row.worldId

  // On a phone the world has a row to itself and the two boxes and the remove button share the one
  // below it, labelled in place; from sm up the row's cells join the list's one-row grid, under its
  // column headings.
  return (
    <div className="grid grid-cols-[1fr_1fr_auto] items-end gap-x-2 gap-y-1.5 sm:contents">
      <span className="col-span-3 flex min-w-0 items-center gap-2 sm:col-span-1">
        <WorldPicture url={row.thumbnailUrl} />
        <span className="min-w-0 [overflow-wrap:anywhere]">{label}</span>
      </span>
      <label className="flex min-w-0 flex-col gap-1">
        <span className="text-muted-foreground sm:hidden">Fewest players</span>
        <Input
          type="number"
          min={1}
          aria-label={`Fewest players, ${label}`}
          value={row.min}
          onChange={(e) => onChange('min', e.target.value)}
        />
      </label>
      <label className="flex min-w-0 flex-col gap-1">
        <span className="text-muted-foreground sm:hidden">Most players</span>
        <Input
          type="number"
          min={1}
          aria-label={`Most players, ${label}`}
          value={row.max}
          onChange={(e) => onChange('max', e.target.value)}
        />
      </label>
      <button
        type="button"
        aria-label={`Remove ${label}`}
        className="grid place-items-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground"
        style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
        onClick={onRemove}
      >
        <X className="size-4" />
      </button>
    </div>
  )
}

/**
 * Adds a world: what is typed searches the worlds Modbot knows, and Add takes it as a world's link
 * or id, which the server reads from VRChat when it has never read that world.
 */
function AddWorld({ onAdd }: { onAdd: (world: CalendarWorld) => void }) {
  const [text, setText] = useState('')
  const [found, setFound] = useState<CalendarWorld[]>([])
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    const typed = text.trim()
    if (typed.length < 2) return

    let cancelled = false
    const timer = setTimeout(() => {
      worldListApi
        .search(typed)
        .then((answer) => {
          if (!cancelled) setFound(answer.slice(0, 8))
        })
        .catch(() => {
          if (!cancelled) setFound([])
        })
    }, SEARCH_AFTER_MS)

    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [text])

  // A search answers what was typed; with too little typed there is nothing to show.
  const shown = text.trim().length >= 2 ? found : []

  const take = (world: CalendarWorld) => {
    onAdd(world)
    setText('')
    setFound([])
    setProblem(null)
  }

  const find = () => {
    if (text.trim() === '') return
    setBusy(true)
    setProblem(null)
    worldListApi
      .find(text)
      .then(take)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not find that world.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex gap-2">
        <Input
          aria-label="Add a world"
          placeholder="World name, link or id"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              find()
            }
          }}
        />
        <Button size="sm" variant="outline" disabled={busy || text.trim() === ''} onClick={find}>
          Add
        </Button>
      </div>
      {problem && <span className="text-destructive">{problem}</span>}
      {shown.length > 0 && (
        <ul className="flex flex-col rounded-sm border border-(length:--hairline)">
          {shown.map((w) => (
            <li key={w.worldId}>
              <button
                type="button"
                className="flex w-full min-h-(--row-h) items-center gap-2 px-2 py-1 text-left hover:bg-muted"
                onClick={() => take(w)}
              >
                <WorldPicture url={w.thumbnailUrl} />
                <span className="min-w-0 [overflow-wrap:anywhere]">{w.name ?? w.worldId}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
