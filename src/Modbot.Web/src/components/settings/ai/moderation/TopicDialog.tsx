import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import {
  failure,
  moderationApi,
  NO_SCOPE,
  type RuleAction,
  type Sensitivity,
  type TopicView,
} from '@/lib/aiModeration'
import { LongField, Outcome } from '../../fields'
import { Group, RuleActionFields } from './RuleFields'

const SENSITIVITIES: { value: Sensitivity; label: string }[] = [
  { value: 'low', label: 'Low' },
  { value: 'medium', label: 'Medium' },
  { value: 'high', label: 'High' },
]

/**
 * Creates or edits an AI topic. `topic` null creates one.
 *
 * The form mounts when the dialog opens, so it starts from the topic every time rather than from
 * whatever was typed the last time it was open.
 */
export function TopicDialog({
  topic,
  open,
  onClose,
  onSaved,
}: {
  topic: TopicView | null
  open: boolean
  onClose: () => void
  onSaved: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      {open && <TopicForm key={topic?.id ?? 'new'} topic={topic} onClose={onClose} onSaved={onSaved} />}
    </Dialog>
  )
}

function TopicForm({
  topic,
  onClose,
  onSaved,
}: {
  topic: TopicView | null
  onClose: () => void
  onSaved: () => void
}) {
  const [name, setName] = useState(topic?.name ?? '')
  const [instructions, setInstructions] = useState(topic?.instructions ?? '')
  const [sensitivity, setSensitivity] = useState<Sensitivity>(topic?.sensitivity ?? 'medium')
  const [rule, setRule] = useState<RuleAction>({
    enabled: topic?.enabled ?? true,
    targets: topic?.targets ?? ['discordMessage'],
    deleteMessage: topic?.deleteMessage ?? false,
    timeoutMinutes: topic?.timeoutMinutes ?? null,
    scope: topic?.scope ?? NO_SCOPE,
    trialDays: topic?.trial?.days ?? null,
  })
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setProblem(null)

    const body = { ...rule, name, instructions, sensitivity }
    const work = topic ? moderationApi.updateTopic(topic.id, body) : moderationApi.createTopic(body)

    work
      .then(() => {
        onSaved()
        onClose()
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not save the topic.')))
      .finally(() => setBusy(false))
  }

  return (
    <DialogContent
      title={topic ? 'Edit AI topic' : 'New AI topic'}
      className="max-w-[640px]"
      bodyClassName="flex max-h-[75vh] flex-col gap-4 overflow-y-auto"
    >
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Name</span>
        <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </label>

      <LongField
        label="What to catch"
        value={instructions}
        placeholder=""
        rows={5}
        onChange={setInstructions}
      />

      <Group label="Sensitivity">
        <div role="radiogroup" aria-label="Sensitivity" className="flex gap-4">
          {SENSITIVITIES.map((s) => (
            <label key={s.value} className="flex items-center gap-2">
              <input
                type="radio"
                name="sensitivity"
                checked={sensitivity === s.value}
                onChange={() => setSensitivity(s.value)}
              />
              {s.label}
            </label>
          ))}
        </div>
      </Group>

      <RuleActionFields value={rule} onChange={setRule} acting={topic?.acting ?? false} />

      <div className="flex items-center gap-2">
        <Button size="sm" disabled={busy} onClick={save}>
          {busy ? 'Saving…' : 'Save'}
        </Button>
        <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
          Cancel
        </Button>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </DialogContent>
  )
}
