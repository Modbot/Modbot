import { useEffect, useMemo, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Checkbox, Field, LongField, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
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

const OTHER_WORLD = '__other__'

/** The create and edit form for one event (calendar design §2). */
export function CalendarEventForm({
  event,
  categories,
  platforms,
  onClose,
  onSaved,
}: {
  event: CalendarEvent | null
  categories: string[]
  platforms: string[]
  onClose: () => void
  onSaved: (saved: CalendarEvent) => void
}) {
  const [input, setInput] = useState<CalendarEventInput>(() => (event ? inputFrom(event) : blankEvent(new Date())))
  const [worlds, setWorlds] = useState<CalendarWorld[]>([])
  const [typedWorld, setTypedWorld] = useState(false)
  // Kept as typed, so a comma can be typed; split when saving.
  const [languages, setLanguages] = useState(() => input.languages.join(', '))
  const [tags, setTags] = useState(() => input.tags.join(', '))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    calendarApi
      .worlds()
      .then(setWorlds)
      .catch(() => setWorlds([]))
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
  const worldChoice = typedWorld || (input.worldId !== null && !knownWorld) ? OTHER_WORLD : (input.worldId ?? '')

  const save = (draft: boolean) => {
    setBusy(true)
    setError(null)

    const body = { ...input, languages: splitList(languages), tags: splitList(tags), draft }
    const request = event ? calendarApi.update(event.id, body) : calendarApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the event.'))
      .finally(() => setBusy(false))
  }

  const isDraft = !event || event.state === 'draft'

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={event ? 'Edit event' : 'New event'}
        className="max-w-[720px]"
        bodyClassName="max-h-[75vh] overflow-y-auto"
      >
        <div className="flex flex-col gap-4">
          <Field label="Title" value={input.title} placeholder="" onChange={(v) => set('title', v)} />
          <LongField label="Description" value={input.description} placeholder="" onChange={(v) => set('description', v)} />

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
                </Select>
              </Labelled>
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

          <Section title="VRChat calendar">
            <Checkbox checked={input.publishToVRChat} onChange={(v) => set('publishToVRChat', v)}>
              Publish to VRChat calendar
            </Checkbox>
            {input.publishToVRChat && (
              <>
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
                  <Field
                    label="VRChat image id"
                    value={input.vrChatImageId ?? ''}
                    placeholder="file_…"
                    onChange={(v) => set('vrChatImageId', v.trim() || null)}
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
              </>
            )}
          </Section>

          <Section title="Discord">
            <Checkbox checked={input.publishToDiscord} onChange={(v) => set('publishToDiscord', v)}>
              Discord event
            </Checkbox>
            <Checkbox checked={input.postToChannel} onChange={(v) => set('postToChannel', v)}>
              Post to channel
            </Checkbox>
            {input.postToChannel && (
              <ChannelPicker
                label="Channel"
                value={input.channelId ?? ''}
                onChange={(id) => set('channelId', id || null)}
                needs={['viewChannel', 'sendMessages', 'embedLinks']}
                allowNone={false}
              />
            )}
          </Section>

          <Section title="Instance">
            <Checkbox checked={input.autoOpen} onChange={(v) => set('autoOpen', v)}>
              Open the instance automatically
            </Checkbox>
            {input.autoOpen && (
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
            )}
          </Section>

          <Outcome tone="problem">{error}</Outcome>

          <div className="flex flex-wrap justify-end gap-2">
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Cancel
            </Button>
            {isDraft && (
              <Button size="sm" variant="outline" disabled={busy} onClick={() => save(true)}>
                Save draft
              </Button>
            )}
            <Button size="sm" disabled={busy} onClick={() => save(false)}>
              {isDraft ? 'Schedule' : 'Save'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
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
