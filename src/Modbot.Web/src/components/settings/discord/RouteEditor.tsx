import { useEffect, useState } from 'react'
import { X } from 'lucide-react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Input } from '@/components/ui/input'
import {
  api,
  ApiError,
  type DiscordRoute,
  type DiscordRouteBody,
  type DiscordRoutePerson,
  type DiscordRoutePlatform,
  type DiscordRoutes,
} from '@/lib/api'
import { Checkbox } from '@/components/ui/checkbox'
import { Field, Outcome } from '../fields'
import { EVENT_POST_NEEDS } from '@/lib/discordLists'
import { vrchatMedia } from '@/lib/vrchatMedia'

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
  const [subjects, setSubjects] = useState<Picked>({
    vrchat: route?.subjectIds ?? [],
    discord: route?.subjectDiscordIds ?? [],
  })
  const [actors, setActors] = useState<Picked>({
    vrchat: route?.actorIds ?? [],
    discord: route?.actorDiscordIds ?? [],
  })
  const [actorAutomatic, setActorAutomatic] = useState(route?.actorAutomatic ?? false)
  const [subjectRoles, setSubjectRoles] = useState<string[]>(route?.subjectVRChatRoleIds ?? [])
  const [actorRoles, setActorRoles] = useState<string[]>(route?.actorVRChatRoleIds ?? [])
  const [actorModbotRoles, setActorModbotRoles] = useState<string[]>(route?.actorModbotRoleIds ?? [])

  // Names for chips: what the list already knew, plus anybody picked from a search since.
  const [people, setPeople] = useState<Map<string, DiscordRoutePerson>>(
    () => new Map(options.people.map((p) => [personKey(p.platform, p.id), p])),
  )
  const remember = (person: DiscordRoutePerson) =>
    setPeople((current) => new Map(current).set(personKey(person.platform, person.id), person))

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
      subjectIds: subjects.vrchat,
      subjectDiscordIds: subjects.discord,
      actorIds: actors.vrchat,
      actorDiscordIds: actors.discord,
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
          value={subjects}
          onChange={setSubjects}
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
          <PeoplePicker label="Done by" value={actors} onChange={setActors} people={people} onPicked={remember} />
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
      <div className="flex max-h-80 flex-col gap-4 overflow-y-auto rounded-sm border border-(length:--hairline) border-input p-(--panel-pad)">
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
                <span className="font-mono text-muted-foreground">
                  {count}/{types.length}
                </span>
              </div>
              <div className="grid gap-x-4 gap-y-1 pl-[calc(var(--control-h)/2_+_0.5rem)] sm:grid-cols-2">
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
  return (
    <Checkbox checked={checked} mixed={mixed} onChange={onChange}>
      <span className="font-medium">{label}</span>
    </Checkbox>
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
              <Chip
                key={option.id}
                on={on}
                onClick={() => onChange(on ? value.filter((id) => id !== option.id) : [...value, option.id])}
              >
                {option.name}
              </Chip>
            )
          })}
        </div>
      )}
    </div>
  )
}

/** People picked for one filter, by the kind of account each id is. */
type Picked = { vrchat: string[]; discord: string[] }

const PLATFORM_LABEL: Record<DiscordRoutePlatform, string> = { vrchat: 'VRChat', discord: 'Discord' }

function personKey(platform: DiscordRoutePlatform, id: string) {
  return `${platform}:${id}`
}

/**
 * People picked by name or id: VRChat accounts from Modbot's stored profiles, Discord accounts
 * from the ones members linked. Anything typed can also be used as either kind of id as it is,
 * since VRChat ids have no fixed shape and most Discord members are not linked. Each account is
 * kept on its own list, so a Discord-only or VRChat-only person can be picked.
 */
function PeoplePicker({
  label,
  value,
  onChange,
  people,
  onPicked,
}: {
  label: string
  value: Picked
  onChange: (picked: Picked) => void
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

  const has = (platform: DiscordRoutePlatform, id: string) => value[platform].includes(id)

  const add = (person: DiscordRoutePerson) => {
    onPicked(person)
    if (!has(person.platform, person.id))
      onChange({ ...value, [person.platform]: [...value[person.platform], person.id] })
    setQuery('')
    setResults([])
  }

  const remove = (platform: DiscordRoutePlatform, id: string) =>
    onChange({ ...value, [platform]: value[platform].filter((v) => v !== id) })

  const shown = term ? results.filter((p) => !has(p.platform, p.id)) : []
  const typed = (['vrchat', 'discord'] as const)
    .filter((platform) => term && !has(platform, term) && !results.some((p) => p.platform === platform && p.id === term))
    .map((platform): DiscordRoutePerson => ({ id: term, name: null, pictureUrl: null, platform }))

  const chips = (['vrchat', 'discord'] as const).flatMap((platform) => value[platform].map((id) => ({ platform, id })))

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {chips.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {chips.map(({ platform, id }) => {
            const name = people.get(personKey(platform, id))?.name ?? id
            return (
              <Badge key={personKey(platform, id)} variant="secondary" className="gap-1 pr-1">
                {name}
                <span className="text-muted-foreground">{PLATFORM_LABEL[platform]}</span>
                <button
                  type="button"
                  aria-label={`Remove ${name}`}
                  className="text-muted-foreground hover:text-foreground"
                  onClick={() => remove(platform, id)}
                >
                  <X className="size-3" />
                </button>
              </Badge>
            )
          })}
        </div>
      )}
      <Input
        value={query}
        placeholder="Search"
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={(e) => {
          if (e.key !== 'Enter') return
          e.preventDefault()
          const first = shown[0] ?? typed[0]
          if (first) add(first)
        }}
      />
      {term && (error || shown.length > 0 || typed.length > 0) && (
        <div className="flex max-h-48 flex-col overflow-y-auto rounded-sm border border-(length:--hairline) bg-card">
          {error && <span className="px-2.5 py-1 text-destructive">{error}</span>}
          {shown.map((person) => (
            <button
              key={personKey(person.platform, person.id)}
              type="button"
              onClick={() => add(person)}
              className="flex items-center gap-2 px-2.5 py-1 text-left hover:bg-muted"
            >
              {person.pictureUrl ? (
                <img src={vrchatMedia(person.pictureUrl)} alt="" className="size-5 shrink-0 rounded-full object-cover" />
              ) : (
                <span className="size-5 shrink-0 rounded-full bg-muted" />
              )}
              <span className="truncate">{person.name ?? person.id}</span>
              <span className="ml-auto shrink-0 text-muted-foreground">{PLATFORM_LABEL[person.platform]}</span>
              <span className="truncate font-mono text-muted-foreground">{person.id}</span>
            </button>
          ))}
          {typed.map((person) => (
            <button
              key={personKey(person.platform, 'typed')}
              type="button"
              onClick={() => add(person)}
              className="px-2.5 py-1 text-left hover:bg-muted"
            >
              Use {person.id} as a {PLATFORM_LABEL[person.platform]} id
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
