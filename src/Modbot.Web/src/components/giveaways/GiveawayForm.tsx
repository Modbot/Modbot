import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RuleBuilder } from '@/components/giveaways/RuleBuilder'
import { Checkbox, Field, LongField, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ApiError } from '@/lib/api'
import {
  WEIGHTING_LABEL,
  blankGiveaway,
  fromLocalInput,
  giveawayApi,
  inputFrom,
  toLocalInput,
  type Giveaway,
  type GiveawayBuilder,
  type GiveawayEntryWay,
  type GiveawayInput,
  type GiveawayPreview,
} from '@/lib/giveaways'

/** The create and edit form for one giveaway, with the rule builder and its preview. */
export function GiveawayForm({
  giveaway,
  onClose,
  onSaved,
}: {
  giveaway: Giveaway | null
  onClose: () => void
  onSaved: (saved: Giveaway) => void
}) {
  const [input, setInput] = useState<GiveawayInput>(() =>
    giveaway ? inputFrom(giveaway) : blankGiveaway(new Date()),
  )
  const [builder, setBuilder] = useState<GiveawayBuilder | null>(null)
  const [preview, setPreview] = useState<GiveawayPreview | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    giveawayApi
      .builder()
      .then(setBuilder)
      .catch(() => setBuilder(null))
  }, [])

  const set = <K extends keyof GiveawayInput>(key: K, value: GiveawayInput[K]) =>
    setInput((current) => ({ ...current, [key]: value }))

  const runPreview = useCallback(() => {
    setPreviewing(true)
    giveawayApi
      .preview({
        rules: input.rules,
        exclusions: input.exclusions,
        weighting: input.weighting,
        weightCap: input.weightCap,
      })
      .then(setPreview)
      .catch((e: unknown) =>
        setPreview({
          total: 0,
          inDraw: 0,
          totalWeight: 0,
          closeCalls: 0,
          fromPolledData: false,
          unanswerable: e instanceof ApiError ? e.message : 'Could not work that out.',
          stopped: false,
          people: [],
        }),
      )
      .finally(() => setPreviewing(false))
  }, [input.rules, input.exclusions, input.weighting, input.weightCap])

  const save = (draft: boolean) => {
    setBusy(true)
    setError(null)

    const body = { ...input, draft }
    const request = giveaway ? giveawayApi.update(giveaway.id, body) : giveawayApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the giveaway.'))
      .finally(() => setBusy(false))
  }

  const isDraft = !giveaway || giveaway.state === 'draft'

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={giveaway ? 'Edit giveaway' : 'New giveaway'}
        className="max-w-[860px]"
        bodyClassName="max-h-[78vh] overflow-y-auto"
      >
        <div className="flex flex-col gap-4">
          <Field label="Name" value={input.name} placeholder="" onChange={(v) => set('name', v)} />
          <LongField label="Prize" value={input.prize} placeholder="" rows={2} onChange={(v) => set('prize', v)} />

          <Section title="When">
            <div className="grid gap-3 sm:grid-cols-3">
              <Labelled label="Opens">
                <Input
                  type="datetime-local"
                  value={toLocalInput(input.opensAt)}
                  onChange={(e) => set('opensAt', fromLocalInput(e.target.value))}
                />
              </Labelled>
              <Labelled label="Closes">
                <Input
                  type="datetime-local"
                  value={toLocalInput(input.closesAt)}
                  onChange={(e) => set('closesAt', fromLocalInput(e.target.value))}
                />
              </Labelled>
              <Labelled label="Draw">
                <Select
                 
                  aria-label="Draw"
                  value={input.drawAt === null ? 'manual' : 'time'}
                  onChange={(choice) => set('drawAt', choice === 'manual' ? null : input.closesAt)}
                >
                  <option value="manual">By hand</option>
                  <option value="time">At a time</option>
                </Select>
              </Labelled>
            </div>

            {input.drawAt !== null && (
              <Labelled label="Draw at">
                <Input
                  type="datetime-local"
                  className="max-w-64"
                  value={toLocalInput(input.drawAt)}
                  onChange={(e) => set('drawAt', fromLocalInput(e.target.value))}
                />
              </Labelled>
            )}
          </Section>

          <Section title="Entering">
            <div className="grid gap-3 sm:grid-cols-3">
              <Labelled label="How people enter">
                <Select
                 
                  aria-label="How people enter"
                  value={input.entryWay}
                  // Reacting needs something to react to, so picking it ticks the channel post and
                  // the tick box is then held on: unticking it would leave a giveaway nobody can
                  // enter, and the server refuses to save one.
                  onChange={(v) =>
                    setInput((current) => ({
                      ...current,
                      entryWay: v as GiveawayEntryWay,
                      postToChannel: v === 'react' ? true : current.postToChannel,
                    }))
                  }
                >
                  <option value="automatic">Everyone who matches</option>
                  <option value="react">React on Discord</option>
                </Select>
              </Labelled>

              {input.entryWay === 'react' && (
                <Labelled label="Emoji">
                  <Input value={input.emoji} onChange={(e) => set('emoji', e.target.value)} />
                </Labelled>
              )}

              <Labelled label="Winners">
                <Input
                  type="number"
                  min={1}
                  max={100}
                  value={input.winnerCount}
                  onChange={(e) => set('winnerCount', Number(e.target.value))}
                />
              </Labelled>
            </div>
          </Section>

          <Section title="Rules">
            <RuleBuilder rule={input.rules} builder={builder} onChange={(rules) => set('rules', rules)} />

            <div className="flex flex-wrap items-center gap-3">
              <Button type="button" variant="outline" size="sm" disabled={previewing} onClick={runPreview}>
                Preview
              </Button>
              {preview && <PreviewLine preview={preview} />}
            </div>
          </Section>

          <Section title="Not eligible">
            <div className="flex flex-wrap gap-4">
              <Checkbox
                checked={input.exclusions.staff}
                onChange={(v) => set('exclusions', { ...input.exclusions, staff: v })}
              >
                Staff
              </Checkbox>
              <Checkbox
                checked={input.exclusions.pastWinners}
                onChange={(v) => set('exclusions', { ...input.exclusions, pastWinners: v })}
              >
                People who have won before
              </Checkbox>
              <Checkbox
                checked={input.exclusions.bannedMembers}
                onChange={(v) => set('exclusions', { ...input.exclusions, bannedMembers: v })}
              >
                Banned members
              </Checkbox>
            </div>
          </Section>

          <Section title="Weighting">
            <div className="grid gap-3 sm:grid-cols-2">
              <Labelled label="Weighted by">
                <Select
                 
                  aria-label="Weighted by"
                  value={input.weighting}
                  onChange={(v) =>
                    setInput((current) => ({
                      ...current,
                      weighting: v,
                      weightCap: v === 'uniform' ? null : current.weightCap,
                    }))
                  }
                >
                  {(builder?.weightings ?? Object.keys(WEIGHTING_LABEL)).map((w) => (
                    <option key={w} value={w}>
                      {WEIGHTING_LABEL[w] ?? w}
                    </option>
                  ))}
                </Select>
              </Labelled>

              {input.weighting !== 'uniform' && (
                <Labelled label="Most one person can hold">
                  <Input
                    type="number"
                    min={1}
                    value={input.weightCap ?? ''}
                    placeholder="No cap"
                    onChange={(e) => set('weightCap', e.target.value === '' ? null : Number(e.target.value))}
                  />
                </Labelled>
              )}
            </div>
          </Section>

          <Section title="Discord">
            <Checkbox
              checked={input.postToChannel}
              onChange={(v) => set('postToChannel', v)}
              disabled={input.entryWay === 'react'}
            >
              Post to channel
            </Checkbox>

            {(input.postToChannel || input.entryWay === 'react') && (
              <ChannelPicker
                label="Channel"
                value={input.channelId ?? ''}
                onChange={(id) => set('channelId', id || null)}
                needs={['viewChannel', 'sendMessages', 'embedLinks']}
                allowNone={false}
              />
            )}
          </Section>

          <Outcome tone="problem">{error}</Outcome>

          <div className="flex flex-wrap justify-end gap-2">
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Close
            </Button>
            {isDraft && (
              <Button size="sm" variant="outline" disabled={busy} onClick={() => save(true)}>
                Save draft
              </Button>
            )}
            <Button size="sm" disabled={busy} onClick={() => save(false)}>
              {isDraft ? 'Open' : 'Save'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}

/**
 * What the preview says, in one line.
 *
 * A figure counted from presence reports is shown as "about", and the close-call count is shown
 * beside it, because a threshold answered from sampled data does not settle a borderline case
 * (M7 §2.3). A rule reaching past the facts Modbot still keeps says so instead of giving a number.
 */
function PreviewLine({ preview }: { preview: GiveawayPreview }) {
  if (preview.unanswerable) {
    return (
      <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
        {preview.unanswerable}
      </span>
    )
  }

  return (
    <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      {preview.fromPolledData && 'about '}
      <span className="font-mono">{preview.inDraw}</span> of <span className="font-mono">{preview.total}</span>
      {preview.closeCalls > 0 && (
        <>
          {' · '}
          <span className="font-mono">{preview.closeCalls}</span> near the line
        </>
      )}
    </span>
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
