import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import {
  failure,
  moderationApi,
  NO_SCOPE,
  type RuleAction,
  type TermInput,
  type TermListDetail,
} from '@/lib/aiModeration'
import { cn } from '@/lib/utils'
import { Checkbox, Outcome } from '../../fields'
import { Group, RuleActionFields } from './RuleFields'

const KINDS: { value: TermInput['kind']; label: string }[] = [
  { value: 'word', label: 'Whole word' },
  { value: 'contains', label: 'Contains' },
  { value: 'regex', label: 'Pattern' },
]

const NEW_LIST: RuleAction = {
  enabled: true,
  targets: ['discordMessage'],
  deleteMessage: false,
  timeoutMinutes: null,
  scope: NO_SCOPE,
  trialDays: null,
  contextMessages: 5,
  checkPictures: false,
  openReviewForEachFlag: false,
}

/**
 * Creates or edits a term list. A local list edits its name and terms; a Modbot Hub list only
 * switches terms off, because its terms belong to the Hub (AI moderation design §2).
 *
 * The form mounts when the dialog opens, so it always starts from the saved list.
 *
 * @param listId Null to create a new local list.
 */
export function TermListDialog({
  listId,
  open,
  picturesAvailable,
  onClose,
  onSaved,
}: {
  listId: string | null
  open: boolean
  picturesAvailable: boolean
  onClose: () => void
  onSaved: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      {open && (
        <TermListForm
          key={listId ?? 'new'}
          listId={listId}
          picturesAvailable={picturesAvailable}
          onClose={onClose}
          onSaved={onSaved}
        />
      )}
    </Dialog>
  )
}

function TermListForm({
  listId,
  picturesAvailable,
  onClose,
  onSaved,
}: {
  listId: string | null
  picturesAvailable: boolean
  onClose: () => void
  onSaved: () => void
}) {
  const [detail, setDetail] = useState<TermListDetail | null>(null)
  const [name, setName] = useState('')
  const [rule, setRule] = useState<RuleAction>(NEW_LIST)
  const [terms, setTerms] = useState<TermInput[]>([{ id: null, kind: 'word', text: '' }])
  const [excluded, setExcluded] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    if (!listId) return

    moderationApi
      .list(listId)
      .then((d) => {
        setDetail(d)
        setName(d.list.name)
        setRule({
          enabled: d.list.enabled,
          targets: d.list.targets,
          deleteMessage: d.list.deleteMessage,
          timeoutMinutes: d.list.timeoutMinutes,
          scope: d.list.scope,
          trialDays: d.list.trial?.days ?? null,
          contextMessages: d.list.contextMessages,
          checkPictures: d.list.checkPictures,
          openReviewForEachFlag: d.list.openReviewForEachFlag,
        })
        setTerms(
          d.terms
            .filter((t) => t.kind !== 'combination')
            .map((t) => ({
              id: t.id,
              kind: t.kind as TermInput['kind'],
              text: (t.kind === 'regex' ? t.pattern : t.text) ?? '',
            })),
        )
        setExcluded(new Set(d.terms.filter((t) => t.excluded).map((t) => t.id)))
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not load the list.')))
  }, [listId])

  const hub = detail?.list.source === 'cloud'

  const save = () => {
    setBusy(true)
    setProblem(null)

    const body = hub
      ? { ...rule, name, excludedTerms: [...excluded] }
      : { ...rule, name, terms: terms.filter((t) => t.text.trim()) }

    const work = listId ? moderationApi.updateList(listId, body) : moderationApi.createList(body)

    work
      .then(() => {
        onSaved()
        onClose()
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not save the list.')))
      .finally(() => setBusy(false))
  }

  const setTerm = (index: number, next: Partial<TermInput>) =>
    setTerms((all) => all.map((t, i) => (i === index ? { ...t, ...next } : t)))

  const shownHubTerms = (detail?.terms ?? []).filter(
    (t) =>
      !filter.trim() ||
      t.label.toLowerCase().includes(filter.trim().toLowerCase()) ||
      (t.category ?? '').toLowerCase().includes(filter.trim().toLowerCase()),
  )

  return (
    <DialogContent
      title={listId ? (hub ? (detail?.list.name ?? 'Term list') : 'Edit term list') : 'New term list'}
      subtitle={hub ? `Modbot Hub · ${detail?.list.hubVersion ?? ''}` : undefined}
      className="max-w-[720px]"
      bodyClassName="flex max-h-[75vh] flex-col gap-4 overflow-y-auto"
    >
      {!hub && (
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Name</span>
          <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
        </label>
      )}

      <RuleActionFields
        value={rule}
        onChange={setRule}
        acting={detail?.list.acting ?? false}
        picturesAvailable={picturesAvailable}
      />

      {hub ? (
        <Group label={`Terms · ${detail ? detail.terms.length - excluded.size : 0} on`}>
          <Input
            placeholder="Filter"
            aria-label="Filter terms"
            className="h-8"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
          <ul className="flex flex-col">
            {shownHubTerms.map((t) => (
              <li
                key={t.id}
                className={cn(
                  'flex items-start gap-2 border-b py-1 last:border-0',
                  excluded.has(t.id) && 'text-muted-foreground',
                )}
                style={{ borderBottomWidth: 'var(--hairline)' }}
                title={t.note ?? undefined}
              >
                <Checkbox
                  checked={!excluded.has(t.id)}
                  onChange={(on) =>
                    setExcluded((all) => {
                      const next = new Set(all)
                      if (on) next.delete(t.id)
                      else next.add(t.id)
                      return next
                    })
                  }
                >
                  <span className="break-all font-mono">{t.label}</span>
                </Checkbox>
                <span className="ml-auto shrink-0 text-muted-foreground">{t.category}</span>
              </li>
            ))}
          </ul>
        </Group>
      ) : (
        <Group label={`Terms · ${terms.filter((t) => t.text.trim()).length}`}>
          {terms.map((term, index) => (
            <div key={term.id ?? `new-${index}`} className="flex items-center gap-2">
              <select
                value={term.kind}
                onChange={(e) => setTerm(index, { kind: e.target.value as TermInput['kind'] })}
                className="h-8 rounded-md border border-input bg-transparent px-2 text-foreground"
                style={{ fontSize: 'var(--text-small)' }}
                aria-label="How it matches"
              >
                {KINDS.map((k) => (
                  <option key={k.value} value={k.value}>
                    {k.label}
                  </option>
                ))}
              </select>
              <Input
                className={cn('h-8 flex-1', term.kind === 'regex' && 'font-mono')}
                value={term.text}
                maxLength={200}
                aria-label="Term"
                onChange={(e) => setTerm(index, { text: e.target.value })}
              />
              <Button
                size="icon-xs"
                variant="ghost"
                aria-label="Remove term"
                title="Remove"
                onClick={() => setTerms((all) => all.filter((_, i) => i !== index))}
              >
                ×
              </Button>
            </div>
          ))}
          <div>
            <Button
              size="xs"
              variant="outline"
              onClick={() => setTerms((all) => [...all, { id: null, kind: 'word', text: '' }])}
            >
              Add term
            </Button>
          </div>
        </Group>
      )}

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
