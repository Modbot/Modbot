import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, api, type ShowcaseEntry, type ShowcaseKind } from '@/lib/api'

const BLANK = {
  kind: 'sponsor' as ShowcaseKind,
  name: '',
  link: '',
  imageUrl: '',
  vrChatGroupId: '',
  groupImageUrl: '',
  groupBannerUrl: '',
  sortOrder: 0,
}

/**
 * The sponsors and early adopters every Modbot shows on its Credits page.
 *
 * Contributors are not here: they come from GitHub, and a second list kept by hand would only go
 * out of date.
 */
export function Showcase() {
  const [items, setItems] = useState<ShowcaseEntry[] | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const [editing, setEditing] = useState<ShowcaseEntry | null>(null)

  const load = useCallback(
    () =>
      api
        .showcase()
        .then((page) => {
          setItems(page.items)
          setFailure(null)
        })
        .catch((e: unknown) => setFailure(e instanceof ApiError ? e.message : 'Could not load the showcase.')),
    [],
  )

  useEffect(() => void load(), [load])

  const remove = async (id: string) => {
    try {
      await api.removeShowcase(id)
      await load()
    } catch (e) {
      setFailure(e instanceof ApiError ? e.message : 'Could not remove it.')
    }
  }

  return (
    <>
      <h1 className="font-display text-lg">Showcase</h1>

      <ShowcaseForm
        key={editing?.id ?? 'new'}
        entry={editing}
        onDone={() => {
          setEditing(null)
          void load()
        }}
      />

      <Card className="gap-0 py-0">
        {failure ? (
          <p className="px-4 py-6 text-destructive">{failure}</p>
        ) : !items ? (
          <p className="px-4 py-6 text-muted-foreground">Loading</p>
        ) : items.length === 0 ? (
          <p className="px-4 py-6 text-center text-muted-foreground">Nothing yet</p>
        ) : (
          <ul>
            {items.map((item) => (
              <li key={item.id} className="flex flex-wrap items-center gap-3 border-b px-4 py-2 last:border-b-0">
                {item.imageUrl && (
                  <img src={item.imageUrl} alt="" className="size-8 rounded-md object-cover" referrerPolicy="no-referrer" />
                )}
                <span className="font-medium">{item.name}</span>
                <span className="text-muted-foreground">{item.kind}</span>
                {item.vrChatGroupId && <span className="font-mono text-muted-foreground">{item.vrChatGroupId}</span>}
                <span className="ml-auto flex gap-2">
                  <Button size="sm" variant="outline" onClick={() => setEditing(item)}>
                    Edit
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => void remove(item.id)}>
                    Remove
                  </Button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </>
  )
}

function ShowcaseForm({ entry, onDone }: { entry: ShowcaseEntry | null; onDone: () => void }) {
  const [form, setForm] = useState(
    entry
      ? {
          kind: entry.kind,
          name: entry.name,
          link: entry.link,
          imageUrl: entry.imageUrl,
          vrChatGroupId: entry.vrChatGroupId ?? '',
          groupImageUrl: entry.groupImageUrl ?? '',
          groupBannerUrl: entry.groupBannerUrl ?? '',
          sortOrder: entry.sortOrder,
        }
      : BLANK,
  )
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)

  const set = (key: keyof typeof BLANK, value: string) =>
    setForm((f) => ({ ...f, [key]: key === 'sortOrder' ? Number(value) || 0 : value }))

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setFailure(null)

    try {
      const body = {
        ...form,
        vrChatGroupId: form.vrChatGroupId || null,
        groupImageUrl: form.groupImageUrl || null,
        groupBannerUrl: form.groupBannerUrl || null,
      }

      if (entry) await api.saveShowcase(entry.id, body)
      else await api.addShowcase(body)

      setForm(BLANK)
      onDone()
    } catch (e) {
      setFailure(e instanceof ApiError ? e.message : 'Could not save it.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="px-6">
      <form onSubmit={save} className="flex flex-col gap-4">
        <h2 className="font-display text-base">{entry ? 'Edit' : 'Add'}</h2>

        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Labelled id="kind" label="Kind">
            <select
              id="kind"
              value={form.kind}
              onChange={(e) => set('kind', e.target.value)}
              className="h-9 w-full rounded-md border border-input bg-background px-2"
            >
              <option value="sponsor">Sponsor</option>
              <option value="early-adopter">Early adopter</option>
            </select>
          </Labelled>
          <Labelled id="name" label="Name">
            <Input id="name" value={form.name} onChange={(e) => set('name', e.target.value)} />
          </Labelled>
          <Labelled id="link" label="Link">
            <Input id="link" value={form.link} onChange={(e) => set('link', e.target.value)} />
          </Labelled>
          <Labelled id="image" label="Image link">
            <Input id="image" value={form.imageUrl} onChange={(e) => set('imageUrl', e.target.value)} />
          </Labelled>
          <Labelled id="group" label="VRChat group id">
            <Input id="group" value={form.vrChatGroupId} onChange={(e) => set('vrChatGroupId', e.target.value)} />
          </Labelled>
          <Labelled id="group-image" label="Group image link">
            <Input id="group-image" value={form.groupImageUrl} onChange={(e) => set('groupImageUrl', e.target.value)} />
          </Labelled>
          <Labelled id="group-banner" label="Group banner link">
            <Input
              id="group-banner"
              value={form.groupBannerUrl}
              onChange={(e) => set('groupBannerUrl', e.target.value)}
            />
          </Labelled>
          <Labelled id="order" label="Order">
            <Input id="order" type="number" value={String(form.sortOrder)} onChange={(e) => set('sortOrder', e.target.value)} />
          </Labelled>
        </div>

        {failure && (
          <p role="alert" className="text-destructive">
            {failure}
          </p>
        )}

        <div className="flex items-center gap-3">
          <Button type="submit" disabled={busy || form.name === ''}>
            {entry ? 'Save' : 'Add'}
          </Button>
          {entry && (
            <Button type="button" variant="ghost" onClick={onDone}>
              Cancel
            </Button>
          )}
        </div>
      </form>
    </Card>
  )
}

function Labelled({ id, label, children }: { id: string; label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="font-medium">
        {label}
      </label>
      {children}
    </div>
  )
}
