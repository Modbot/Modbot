import { useEffect, useRef, useState } from 'react'
import { X } from 'lucide-react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, ApiError, type DiscordRoute, type DiscordRouteBody, type DiscordRoutePerson, type DiscordRoutes } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Checkbox, Field, Outcome } from '../fields'
import { EVENT_POST_NEEDS } from '@/lib/discordLists'

/**
 * One route, new or existing: the channel, the events, and who they must be about or done by.
 *
 * Every filter is saved whole, so clearing one sends an empty list and the server clears it.
 */
export function RouteEditor({
  route,
  options,
  onCancel,
  onSaved,
}: {
  route: DiscordRoute | null
  options: DiscordRoutes
  onCancel: () => void
  onSaved: () => void
}) {
  const [channelId, setChannelId] = useState(route?.channelId ?? '')
  const [name, setName] = useState(route?.name ?? '')
  const [eventTypes, setEventTypes] = useState<string[]>(route?.eventTypes ?? [])
  const [subjectIds, setSubjectIds] = useState<string[]>(route?.subjectIds ?? [])
  const [actorIds, setActorIds] = useState<string[]>(route?.actorIds ?? [])
  const [actorAutomatic, setActorAutomatic] = useState(route?.actorAutomatic ?? false)
  const [subjectRoles, setSubjectRoles] = useState<string[]>(route?.subjectVRChatRoleIds ?? [])
  const [actorRoles, setActorRoles] = useState<string[]>(route?.actorVRChatRoleIds ?? [])
  const [actorModbotRoles, setActorModbotRoles] = useState<string[]>(route?.actorModbotRoleIds ?? [])

  // Names for chips: what the list already knew, plus anybody picked from a search since.
  const [people, setPeople] = useState<Map<string, DiscordRoutePerson>>(
    () => new Map(options.people.map((p) => [p.id, p])),
  )
  const remember = (person: DiscordRoutePerson) => setPeople((current) => new Map(current).set(person.id, person))

  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)

    const body: DiscordRouteBody = {
      name,
      channelId,
      eventTypes,
      subjectIds,
      actorIds,
      actorAutomatic,
      subjectVRChatRoleIds: subjectRoles,
      actorVRChatRoleIds: actorRoles,
      actorModbotRoleIds: actorModbotRoles,
    }

    const request = route ? api.updateDiscordRoute(route.id, body) : api.createDiscordRoute(body)

    request
      .then(() => onSaved())
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  return (
    <form onSubmit={save} className="flex flex-col gap-5">
      <div className="grid gap-3 sm:grid-cols-2">
        <ChannelPicker label="Channel" value={channelId} onChange={setChannelId} needs={EVENT_POST_NEEDS} allowNone={false} />
        <Field label="Name" value={name} onChange={setName} placeholder="" />
      </div>

      <EventPicker groups={options.eventGroups} value={eventTypes} onChange={setEventTypes} />

      <div className="flex flex-col gap-4">
        <PeoplePicker
          label="Happened to"
          value={subjectIds}
          onChange={setSubjectIds}
          people={people}
          onPicked={remember}
        />
        <Chips
          label="Happened to someone with these VRChat roles"
          options={options.vrChatRoles}
          value={subjectRoles}
          onChange={setSubjectRoles}
        />
        <div className="flex flex-col gap-2">
          <PeoplePicker label="Done by" value={actorIds} onChange={setActorIds} people={people} onPicked={remember} />
          <Checkbox checked={actorAutomatic} onChange={setActorAutomatic}>
            Modbot (automatic)
          </Checkbox>
        </div>
        <Chips
          label="Done by someone with these VRChat roles"
          options={options.vrChatRoles}
          value={actorRoles}
          onChange={setActorRoles}
        />
        <Chips
          label="Done by someone with these Modbot roles"
          options={options.modbotRoles}
          value={actorModbotRoles}
          onChange={setActorModbotRoles}
        />
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <Button type="submit" size="sm" disabled={saving}>
          {saving ? 'Saving…' : 'Save'}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Outcome tone="problem">{error}</Outcome>
      </div>
    </form>
  )
}

/** Event types by group, each group with a box that ticks all of it. */
function EventPicker({
  groups,
  value,
  onChange,
}: {
  groups: DiscordRoutes['eventGroups']
  value: string[]
  onChange: (types: string[]) => void
}) {
  const chosen = new Set(value)

  const set = (types: string[], on: boolean) => {
    const next = new Set(chosen)
    for (const type of types) {
      if (on) next.add(type)
      else next.delete(type)
    }
    onChange([...next])
  }

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">Events</span>
      <div className="flex max-h-80 flex-col gap-4 overflow-y-auto rounded-md border p-3">
        {groups.map((group) => {
          const types = group.types.map((t) => t.type)
          const count = types.filter((t) => chosen.has(t)).length
          return (
            <div key={group.name} className="flex flex-col gap-1.5">
              <div className="flex items-center gap-2">
                <GroupBox
                  checked={count === types.length}
                  mixed={count > 0 && count < types.length}
                  onChange={(on) => set(types, on)}
                  label={group.name}
                />
                <span className="text-muted-foreground tabular-nums">
                  {count}/{types.length}
                </span>
              </div>
              <div className="grid gap-x-4 gap-y-1 pl-5 sm:grid-cols-2">
                {group.types.map((t) => (
                  <Checkbox key={t.type} checked={chosen.has(t.type)} onChange={(on) => set([t.type], on)}>
                    {t.label}
                  </Checkbox>
                ))}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function GroupBox({
  checked,
  mixed,
  label,
  onChange,
}: {
  checked: boolean
  mixed: boolean
  label: string
  onChange: (checked: boolean) => void
}) {
  const ref = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (ref.current) ref.current.indeterminate = mixed
  }, [mixed])

  return (
    <label className="flex items-center gap-2 font-medium">
      <input ref={ref} type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  )
}

/** Toggle chips for a short list: roles. */
function Chips({
  label,
  options,
  value,
  onChange,
}: {
  label: string
  options: { id: string; name: string }[]
  value: string[]
  onChange: (ids: string[]) => void
}) {
  const chosen = new Set(value)

  // A saved id no longer offered -- a deleted role -- stays visible so it can be taken off.
  const all = [
    ...options,
    ...value.filter((id) => !options.some((o) => o.id === id)).map((id) => ({ id, name: id })),
  ]

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {all.length === 0 ? (
        <span className="text-muted-foreground">None</span>
      ) : (
        <div className="flex flex-wrap gap-1.5">
          {all.map((option) => {
            const on = chosen.has(option.id)
            return (
              <button
                key={option.id}
                type="button"
                aria-pressed={on}
                onClick={() => onChange(on ? value.filter((id) => id !== option.id) : [...value, option.id])}
                className={cn(
                  'rounded-full border px-2.5 py-0.5 text-xs transition-colors',
                  on ? 'border-primary bg-primary text-primary-foreground' : 'hover:bg-accent',
                )}
              >
                {option.name}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}

/**
 * People picked by name or id from Modbot's stored profiles. Anything typed can also be used as an
 * id as it is, since VRChat ids have no fixed shape and a person may not have a stored profile.
 */
function PeoplePicker({
  label,
  value,
  onChange,
  people,
  onPicked,
}: {
  label: string
  value: string[]
  onChange: (ids: string[]) => void
  people: Map<string, DiscordRoutePerson>
  onPicked: (person: DiscordRoutePerson) => void
}) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<DiscordRoutePerson[]>([])
  const [error, setError] = useState<string | null>(null)

  const term = query.trim()

  useEffect(() => {
    if (!term) return

    let cancelled = false
    const timer = window.setTimeout(() => {
      api
        .discordRoutePeople(term)
        .then((found) => {
          if (cancelled) return
          setResults(found.people)
          setError(null)
        })
        .catch(() => {
          if (!cancelled) setError('Could not search.')
        })
    }, 250)

    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [term])

  const add = (person: DiscordRoutePerson) => {
    onPicked(person)
    if (!value.includes(person.id)) onChange([...value, person.id])
    setQuery('')
    setResults([])
  }

  const shown = term ? results.filter((p) => !value.includes(p.id)) : []
  const typed = term && !value.includes(term) && !results.some((p) => p.id === term) ? term : null

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {value.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {value.map((id) => (
            <span key={id} className="inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs">
              {people.get(id)?.name ?? id}
              <button
                type="button"
                aria-label={`Remove ${people.get(id)?.name ?? id}`}
                className="text-muted-foreground hover:text-foreground"
                onClick={() => onChange(value.filter((v) => v !== id))}
              >
                <X className="size-3" />
              </button>
            </span>
          ))}
        </div>
      )}
      <Input
        value={query}
        placeholder="Search"
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={(e) => {
          if (e.key !== 'Enter') return
          e.preventDefault()
          const first = shown[0] ?? (typed ? { id: typed, name: null, pictureUrl: null } : null)
          if (first) add(first)
        }}
      />
      {term && (error || shown.length > 0 || typed) && (
        <div className="flex max-h-48 flex-col overflow-y-auto rounded-md border p-1">
          {error && <span className="px-2 py-1 text-destructive">{error}</span>}
          {shown.map((person) => (
            <button
              key={person.id}
              type="button"
              onClick={() => add(person)}
              className="flex items-center gap-2 rounded-sm px-2 py-1 text-left hover:bg-accent"
            >
              {person.pictureUrl ? (
                <img src={person.pictureUrl} alt="" className="size-5 shrink-0 rounded-full object-cover" />
              ) : (
                <span className="size-5 shrink-0 rounded-full bg-secondary" />
              )}
              <span className="truncate">{person.name ?? person.id}</span>
              <span className="ml-auto truncate text-xs text-muted-foreground">{person.id}</span>
            </button>
          ))}
          {typed && (
            <button
              type="button"
              onClick={() => add({ id: typed, name: null, pictureUrl: null })}
              className="rounded-sm px-2 py-1 text-left hover:bg-accent"
            >
              Use {typed}
            </button>
          )}
        </div>
      )}
    </div>
  )
}
