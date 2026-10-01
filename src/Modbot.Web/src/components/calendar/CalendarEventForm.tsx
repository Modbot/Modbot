import { useEffect, useMemo, useState } from 'react'
import { Check } from 'lucide-react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Checkbox, Field, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Tabs } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import {
  blankEvent,
  calendarApi,
  CATEGORY_LABEL,
  DAYS,
  inputFrom,
  PLATFORM_LABEL,
  type CalendarEvent,
  type CalendarEventInput,
  type CalendarRepeat,
  type CalendarWorld,
} from '@/lib/calendar'
import {
  counted,
  DESCRIPTION_LIMIT,
  DESTINATION_LABEL,
  DESTINATION_SWITCH,
  DESTINATIONS,
  isSetUp,
  missingChannel,
  TITLE_LIMIT,
  type CalendarDestination,
  type CalendarReady,
} from '@/lib/calendarPlaces'
import { cn } from '@/lib/utils'
import { EventPreview } from './EventPreview'
import { NotSetUp } from './NotSetUp'
import { worldListApi, type WorldList } from '@/lib/worldLists'
import { VRChatPictureField } from './VRChatPictureField'

const OTHER_WORLD = '__other__'
const FROM_LIST = '__list__'

type Tab = 'details' | 'preview'

/**
 * The create and edit form for one event (calendar design §2, §14).
 *
 * Where the event goes comes first, as one row of chips: each place it can go, on or off, the
 * calendar feed shown and always on. A place's own settings are in its section below and only while
 * its chip is on. A ticked place that cannot work as things are set up says "Not set up" beside its
 * chip, linking to where it is set up. Preview draws the event the way each place that is on would
 * show it. The buttons are pinned under the form, so a phone never scrolls them away.
 */
export function CalendarEventForm({
  event,
  initial,
  categories,
  platforms,
  ready,
  onClose,
  onSaved,
}: {
  event: CalendarEvent | null
  /** What a new event starts with: a time drawn on the calendar, or a copy of another event. */
  initial?: CalendarEventInput
  categories: string[]
  platforms: string[]
  /** Which places are set up, from the calendar's own read. */
  ready?: CalendarReady | null
  onClose: () => void
  onSaved: (saved: CalendarEvent) => void
}) {
  const [input, setInput] = useState<CalendarEventInput>(() => initial ?? (event ? inputFrom(event) : blankEvent(new Date())))
  const [worlds, setWorlds] = useState<CalendarWorld[]>([])
  const [typedWorld, setTypedWorld] = useState(false)
  const [lists, setLists] = useState<WorldList[]>([])
  const [pickingList, setPickingList] = useState(false)
  // Set once Save or Schedule was refused for a list not chosen, so the field says so.
  const [listMissed, setListMissed] = useState(false)
  // Kept as typed, so a comma can be typed; split when saving.
  const [languages, setLanguages] = useState(() => input.languages.join(', '))
  const [tags, setTags] = useState(() => input.tags.join(', '))
  const [busy, setBusy] = useState(false)
  // A VRChat picture on its way up: saving now would save the event without it.
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('details')
  // What Preview draws: the form as it was when Preview was opened.
  const [shown, setShown] = useState<CalendarEventInput | null>(null)

  useEffect(() => {
    calendarApi
      .worlds()
      .then(setWorlds)
      .catch(() => setWorlds([]))
    worldListApi
      .all()
      .then((answer) => setLists(answer.lists))
      .catch(() => setLists([]))
  }, [])

  const zones = useMemo(() => {
    try {
      return Intl.supportedValuesOf('timeZone')
    } catch {
      return ['UTC']
    }
  }, [])

  const set = <K extends keyof CalendarEventInput>(key: K, value: CalendarEventInput[K]) =>
    setInput((current) => ({ ...current, [key]: value }))

  const toggle = (key: 'repeatDays' | 'platforms', value: string) =>
    setInput((current) => ({
      ...current,
      [key]: current[key].includes(value) ? current[key].filter((v) => v !== value) : [...current[key], value],
    }))

  const knownWorld = input.worldId !== null && worlds.some((w) => w.worldId === input.worldId)
  const fromList = pickingList || Boolean(input.worldListId)
  const worldChoice = fromList
    ? FROM_LIST
    : typedWorld || (input.worldId !== null && !knownWorld)
      ? OTHER_WORLD
      : (input.worldId ?? '')

  const body = (draft: boolean): CalendarEventInput => ({
    ...input,
    languages: splitList(languages),
    tags: splitList(tags),
    draft,
  })

  // "Pick from a list" with no list chosen would save as an event with no world at all.
  const listMissing = worldChoice === FROM_LIST && !input.worldListId

  const save = (draft: boolean) => {
    if (listMissing) {
      setListMissed(true)
      setTab('details')
      return
    }

    setBusy(true)
    setError(null)

    const request = event ? calendarApi.update(event.id, body(draft)) : calendarApi.create(body(draft))

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the event.'))
      .finally(() => setBusy(false))
  }

  const pickTab = (next: Tab) => {
    if (next === 'preview') setShown(body(false))
    setTab(next)
  }

  const isDraft = !event || event.state === 'draft'
  const on = (place: CalendarDestination) => place === 'feed' || input[DESTINATION_SWITCH[place]]
  const title = counted(input.title, TITLE_LIMIT)
  const description = counted(input.description, DESCRIPTION_LIMIT)

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={event ? 'Edit event' : 'New event'}
        className="max-w-[720px]"
        foot={
          <DialogFoot>
            {error && (
              <span className="mr-auto min-w-0 basis-full sm:basis-auto">
                <Outcome tone="problem">{error}</Outcome>
              </span>
            )}
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Cancel
            </Button>
            {isDraft && (
              <Button size="sm" variant="outline" disabled={busy || uploading} onClick={() => save(true)}>
                Save draft
              </Button>
            )}
            <Button size="sm" disabled={busy || uploading} onClick={() => save(false)}>
              {isDraft ? 'Schedule' : 'Save'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-4">
          <div role="group" aria-label="Where it goes" className="flex flex-wrap gap-2">
            {DESTINATIONS.map((place) => (
              <span key={place} className="inline-flex items-center gap-1.5">
                <Chip
                  on={on(place)}
                  disabled={place === 'feed'}
                  onClick={() => {
                    if (place !== 'feed') set(DESTINATION_SWITCH[place], !input[DESTINATION_SWITCH[place]])
                  }}
                >
                  {on(place) && <Check className="size-3.5" />}
                  {DESTINATION_LABEL[place]}
                </Chip>
                {place !== 'feed' && on(place) && !isSetUp(place, ready) ? (
                  <NotSetUp place={place} newTab />
                ) : (
                  // The channel is picked in its own section, just below.
                  place === 'channelPost' &&
                  missingChannel(input) && (
                    <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                      Not set up
                    </span>
                  )
                )}
              </span>
            ))}
          </div>

          <Tabs
            value={tab}
            onChange={pickTab}
            tabs={[
              { value: 'details', label: 'Details' },
              { value: 'preview', label: 'Preview' },
            ]}
            panelClassName="overflow-visible pt-4"
          >
            {tab === 'preview' && shown ? (
              <EventPreview eventId={event?.id ?? null} input={shown} places={DESTINATIONS.filter(on)} />
            ) : (
              <div className="flex flex-col gap-4">
                <Counted label="Title" count={title}>
                  <Input value={input.title} onChange={(e) => set('title', e.target.value)} />
                </Counted>
                <Counted label="Description" count={description}>
                  <Textarea rows={3} value={input.description} onChange={(e) => set('description', e.target.value)} />
                </Counted>

                <Section title="When">
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Labelled label="Starts">
                      <Input type="datetime-local" value={input.startsAt} onChange={(e) => set('startsAt', e.target.value)} />
                    </Labelled>
                    <Labelled label="Ends">
                      <Input type="datetime-local" value={input.endsAt} onChange={(e) => set('endsAt', e.target.value)} />
                    </Labelled>
                    <Labelled label="Time zone">
                      <Select value={input.timeZone} onChange={(v) => set('timeZone', v)} aria-label="Time zone">
                        {!zones.includes(input.timeZone) && <option value={input.timeZone}>{input.timeZone}</option>}
                        {zones.map((z) => (
                          <option key={z} value={z}>
                            {z}
                          </option>
                        ))}
                      </Select>
                    </Labelled>
                    <Labelled label="Repeat">
                      <Select value={input.repeat} onChange={(v) => set('repeat', v as CalendarRepeat)} aria-label="Repeat">
                        <option value="none">Does not repeat</option>
                        <option value="daily">Daily</option>
                        <option value="weekly">Weekly</option>
                        <option value="monthly">Monthly</option>
                      </Select>
                    </Labelled>
                  </div>

                  {input.repeat === 'weekly' && (
                    <div className="flex flex-wrap gap-3">
                      {DAYS.map((d) => (
                        <Checkbox key={d.value} checked={input.repeatDays.includes(d.value)} onChange={() => toggle('repeatDays', d.value)}>
                          {d.label}
                        </Checkbox>
                      ))}
                    </div>
                  )}

                  {input.repeat !== 'none' && (
                    <Labelled label="Last date">
                      <Input
                        type="date"
                        value={input.repeatUntil ?? ''}
                        onChange={(e) => set('repeatUntil', e.target.value || null)}
                      />
                    </Labelled>
                  )}
                </Section>

                <Section title="Where">
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Labelled label="World">
                      <Select
                        aria-label="World"
                        value={worldChoice}
                        onChange={(v) => {
                          if (v === FROM_LIST) {
                            setTypedWorld(false)
                            setPickingList(true)
                            // A world typed or picked before is not this event's world any more.
                            set('worldId', null)
                            return
                          }

                          setPickingList(false)
                          set('worldListId', null)

                          if (v === OTHER_WORLD) {
                            setTypedWorld(true)
                            return
                          }

                          setTypedWorld(false)
                          set('worldId', v || null)
                        }}
                      >
                        <option value="">None</option>
                        {worlds.map((w) => (
                          <option key={w.worldId} value={w.worldId}>
                            {w.name ?? w.worldId}
                          </option>
                        ))}
                        <option value={OTHER_WORLD}>World id</option>
                        <option value={FROM_LIST}>Pick from a list</option>
                      </Select>
                    </Labelled>
                    {worldChoice === FROM_LIST && (
                      <Labelled label="World list">
                        <Select
                          aria-label="World list"
                          value={input.worldListId ?? ''}
                          onChange={(v) => {
                            // The list picks the world, date by date: a fixed world left over would be stale.
                            setInput((current) => ({ ...current, worldListId: v || null, worldId: null }))
                          }}
                        >
                          <option value="">—</option>
                          {lists.map((l) => (
                            <option key={l.id} value={l.id}>
                              {l.name}
                            </option>
                          ))}
                        </Select>
                        {listMissed && listMissing && (
                          <span role="alert" className="text-destructive">
                            Pick a list
                          </span>
                        )}
                      </Labelled>
                    )}
                    {worldChoice === OTHER_WORLD && (
                      <Field label="World id" value={input.worldId ?? ''} placeholder="wrld_…" onChange={(v) => set('worldId', v.trim() || null)} />
                    )}
                    <Labelled label="Who can join">
                      <Select value={input.accessType} onChange={(v) => set('accessType', v)} aria-label="Who can join">
                        <option value="members">Group members</option>
                        <option value="plus">Members and their friends</option>
                        <option value="public">Anyone</option>
                      </Select>
                    </Labelled>
                    <Labelled label="Region">
                      <Select value={input.region} onChange={(v) => set('region', v)} aria-label="Region">
                        <option value="us">US West</option>
                        <option value="use">US East</option>
                        <option value="eu">Europe</option>
                        <option value="jp">Japan</option>
                      </Select>
                    </Labelled>
                  </div>
                  <Field label="Picture link" value={input.imageUrl ?? ''} placeholder="https://" onChange={(v) => set('imageUrl', v.trim() || null)} />
                </Section>

                {input.publishToVRChat && (
                  <Section title={DESTINATION_LABEL.vrchat}>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Labelled label="Category">
                        <Select value={input.category} onChange={(v) => set('category', v)} aria-label="Category">
                          {categories.map((c) => (
                            <option key={c} value={c}>
                              {CATEGORY_LABEL[c] ?? c}
                            </option>
                          ))}
                        </Select>
                      </Labelled>
                      <Labelled label="Visible to">
                        <Select value={input.visibility} onChange={(v) => set('visibility', v)} aria-label="Visible to">
                          <option value="group">Group</option>
                          <option value="public">Everyone</option>
                        </Select>
                      </Labelled>
                      <Field label="Languages" value={languages} placeholder="eng, jpn" onChange={setLanguages} />
                      <Field label="Tags" value={tags} placeholder="" onChange={setTags} />
                      <VRChatPictureField
                        eventId={event?.id ?? null}
                        value={input.vrChatImageId}
                        onChange={(id) => set('vrChatImageId', id)}
                        onUploading={setUploading}
                      />
                    </div>
                    <div className="flex flex-wrap gap-3">
                      {platforms.map((p) => (
                        <Checkbox key={p} checked={input.platforms.includes(p)} onChange={() => toggle('platforms', p)}>
                          {PLATFORM_LABEL[p] ?? p}
                        </Checkbox>
                      ))}
                    </div>
                    <Checkbox checked={input.notifyMembers} onChange={(v) => set('notifyMembers', v)}>
                      Notify group members
                    </Checkbox>
                  </Section>
                )}

                {input.postToChannel && (
                  <Section title={DESTINATION_LABEL.channelPost}>
                    <ChannelPicker
                      label="Channel"
                      value={input.channelId ?? ''}
                      onChange={(id) => set('channelId', id || null)}
                      needs={['viewChannel', 'sendMessages', 'embedLinks']}
                      allowNone={false}
                    />
                  </Section>
                )}

                {input.autoOpen && (
                  <Section title={DESTINATION_LABEL.instance}>
                    <Labelled label="Minutes early">
                      <Input
                        type="number"
                        min={0}
                        max={120}
                        className="w-28"
                        value={input.openMinutesBefore}
                        onChange={(e) => set('openMinutesBefore', Number(e.target.value))}
                      />
                    </Labelled>
                  </Section>
                )}
              </div>
            )}
          </Tabs>
        </div>
      </DialogContent>
    </Dialog>
  )
}

/** A field with its count beside the label: "12 / 100", red once it is over. */
function Counted({
  label,
  count,
  children,
}: {
  label: string
  count: { label: string; over: boolean }
  children: React.ReactNode
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="flex items-baseline justify-between gap-2">
        <span className="text-muted-foreground">{label}</span>
        <span className={cn('font-mono', count.over ? 'text-destructive' : 'text-muted-foreground')}>{count.label}</span>
      </span>
      {children}
    </label>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-3 border-t border-t-(length:--hairline) pt-3">
      <legend className="pr-2 font-label">{title}</legend>
      {children}
    </fieldset>
  )
}

function Labelled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function splitList(text: string): string[] {
  return text
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
}
