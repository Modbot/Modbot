import { useCallback, useEffect, useState } from 'react'
import { X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Field, LongField, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Tabs } from '@/components/ui/tabs'
import { ApiError } from '@/lib/api'
import {
  RULE_LIMITS,
  TERM_LIMITS,
  ruleApi,
  termApi,
  type Rule,
  type Term,
} from '@/lib/termsAndRules'
import { PageMessage } from '@/pages/analytics/shared'

type Tab = 'terms' | 'rules'

const TABS: { value: Tab; label: string }[] = [
  { value: 'terms', label: 'Terms' },
  { value: 'rules', label: 'Rules' },
]

/**
 * Terms and rules: the words the community uses and what they mean, and the rules for members.
 * `/term` and `/rule` in Discord answer from what is typed here. Anybody signed in reads it;
 * changing it needs Change settings, which the server decides and sends as `canEdit`.
 */
export function TermsAndRules() {
  const [tab, setTab] = useState<Tab>('terms')

  return (
    <Tabs value={tab} onChange={setTab} tabs={TABS} panelClassName="overflow-visible pt-3">
      {tab === 'terms' ? <TermsTab /> : <RulesTab />}
    </Tabs>
  )
}

function loadFailed(e: unknown, what: string): string {
  return e instanceof ApiError ? e.message : `Could not load the ${what}.`
}

// ── Terms ──────────────────────────────────────────────────────────────────────────────────────

function TermsTab() {
  const [terms, setTerms] = useState<Term[] | null>(null)
  const [canEdit, setCanEdit] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<Term | 'new' | null>(null)
  const [deleting, setDeleting] = useState<Term | null>(null)

  const load = useCallback(() => {
    termApi
      .all()
      .then((answer) => {
        setTerms(answer.terms)
        setCanEdit(answer.canEdit)
        setError(null)
      })
      .catch((e: unknown) => setError(loadFailed(e, 'terms')))
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!terms) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      {canEdit && (
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex-1" />
          <Button onClick={() => setEditing('new')}>New term</Button>
        </div>
      )}

      {terms.length === 0 ? (
        <PageMessage>No terms.</PageMessage>
      ) : (
        terms.map((term) => (
          <Card key={term.id} className="flex flex-col">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-b-(length:--hairline) px-(--panel-pad) py-1.5">
              <span className="font-medium [overflow-wrap:anywhere]">{term.name}</span>
              {term.otherNames.length > 0 && (
                <span className="text-muted-foreground [overflow-wrap:anywhere]" style={{ fontSize: 'var(--text-small)' }}>
                  {term.otherNames.join(', ')}
                </span>
              )}
              <div className="flex-1" />
              {canEdit && (
                <span className="flex gap-2">
                  <Button size="sm" variant="outline" onClick={() => setEditing(term)}>
                    Edit
                  </Button>
                  <Button size="sm" variant="destructive" onClick={() => setDeleting(term)}>
                    Delete
                  </Button>
                </span>
              )}
            </div>
            <p className="whitespace-pre-wrap px-(--panel-pad) py-2 [overflow-wrap:anywhere]" style={{ fontSize: 'var(--text-small)' }}>
              {term.meaning}
            </p>
          </Card>
        ))
      )}

      {editing && (
        <TermForm
          term={editing === 'new' ? null : editing}
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
        title={`Delete the term “${deleting?.name ?? ''}”?`}
        action="Delete"
        failed="Could not delete the term."
        onConfirm={() => (deleting ? termApi.remove(deleting.id) : Promise.resolve())}
        onDone={() => {
          setDeleting(null)
          load()
        }}
      />
    </div>
  )
}

function TermForm({ term, onClose, onSaved }: { term: Term | null; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(term?.name ?? '')
  const [otherNames, setOtherNames] = useState<string[]>(term?.otherNames ?? [])
  const [meaning, setMeaning] = useState(term?.meaning ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setError(null)

    const body = { name, otherNames: otherNames.filter((o) => o.trim() !== ''), meaning }
    const request = term ? termApi.update(term.id, body) : termApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the term.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={term ? 'Edit term' : 'New term'}
        className="max-w-[640px]"
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
          <Field label="Term" value={name} maxLength={TERM_LIMITS.name} onChange={setName} />

          <div className="flex flex-col gap-2">
            <span className="text-muted-foreground">Other names</span>
            {otherNames.map((other, at) => (
              <div key={at} className="flex gap-2">
                <Input
                  aria-label={`Other name ${at + 1}`}
                  maxLength={TERM_LIMITS.otherName}
                  value={other}
                  onChange={(e) => setOtherNames((current) => current.map((o, i) => (i === at ? e.target.value : o)))}
                />
                <button
                  type="button"
                  aria-label={`Remove other name ${at + 1}`}
                  className="grid shrink-0 place-items-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground"
                  style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
                  onClick={() => setOtherNames((current) => current.filter((_, i) => i !== at))}
                >
                  <X className="size-4" />
                </button>
              </div>
            ))}
            {otherNames.length < TERM_LIMITS.otherNames && (
              <div>
                <Button size="sm" variant="outline" onClick={() => setOtherNames((current) => [...current, ''])}>
                  Add other name
                </Button>
              </div>
            )}
          </div>

          <LongField label="Meaning" value={meaning} placeholder="" rows={5} maxLength={TERM_LIMITS.meaning} onChange={setMeaning} />
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ── Rules ──────────────────────────────────────────────────────────────────────────────────────

function RulesTab() {
  const [rules, setRules] = useState<Rule[] | null>(null)
  const [canEdit, setCanEdit] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [moveError, setMoveError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [editing, setEditing] = useState<Rule | 'new' | null>(null)
  const [deleting, setDeleting] = useState<Rule | null>(null)

  const load = useCallback(() => {
    ruleApi
      .all()
      .then((answer) => {
        setRules(answer.rules)
        setCanEdit(answer.canEdit)
        setError(null)
      })
      .catch((e: unknown) => setError(loadFailed(e, 'rules')))
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const move = (rule: Rule, direction: 'up' | 'down') => {
    setBusy(true)
    setMoveError(null)

    ruleApi
      .move(rule.id, direction)
      .then((answer) => setRules(answer.rules))
      .catch((e: unknown) => setMoveError(e instanceof ApiError ? e.message : 'Could not move the rule.'))
      .finally(() => setBusy(false))
  }

  if (!rules) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      {canEdit && (
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex-1" />
          <Outcome tone="problem">{moveError}</Outcome>
          <Button onClick={() => setEditing('new')}>New rule</Button>
        </div>
      )}

      {rules.length === 0 ? (
        <PageMessage>No rules.</PageMessage>
      ) : (
        rules.map((rule, index) => (
          <Card key={rule.id} className="flex flex-col">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-b-(length:--hairline) px-(--panel-pad) py-1.5">
              <span className="font-mono text-muted-foreground">{rule.number}</span>
              <span className="font-medium [overflow-wrap:anywhere]">{rule.title}</span>
              <div className="flex-1" />
              {canEdit && (
                <span className="flex items-center gap-2">
                  <Button
                    size="icon-xs"
                    variant="ghost"
                    disabled={busy || index === 0}
                    onClick={() => move(rule, 'up')}
                    aria-label={`Move ${rule.title} up`}
                    title="Move up"
                  >
                    ↑
                  </Button>
                  <Button
                    size="icon-xs"
                    variant="ghost"
                    disabled={busy || index === rules.length - 1}
                    onClick={() => move(rule, 'down')}
                    aria-label={`Move ${rule.title} down`}
                    title="Move down"
                  >
                    ↓
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => setEditing(rule)}>
                    Edit
                  </Button>
                  <Button size="sm" variant="destructive" onClick={() => setDeleting(rule)}>
                    Delete
                  </Button>
                </span>
              )}
            </div>
            <p className="whitespace-pre-wrap px-(--panel-pad) py-2 [overflow-wrap:anywhere]" style={{ fontSize: 'var(--text-small)' }}>
              {rule.text}
            </p>
          </Card>
        ))
      )}

      {editing && (
        <RuleForm
          rule={editing === 'new' ? null : editing}
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
        title={`Delete the rule “${deleting?.title ?? ''}”?`}
        action="Delete"
        failed="Could not delete the rule."
        onConfirm={() => (deleting ? ruleApi.remove(deleting.id) : Promise.resolve())}
        onDone={() => {
          setDeleting(null)
          load()
        }}
      />
    </div>
  )
}

function RuleForm({ rule, onClose, onSaved }: { rule: Rule | null; onClose: () => void; onSaved: () => void }) {
  const [title, setTitle] = useState(rule?.title ?? '')
  const [text, setText] = useState(rule?.text ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setError(null)

    const body = { title, text }
    const request = rule ? ruleApi.update(rule.id, body) : ruleApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the rule.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={rule ? 'Edit rule' : 'New rule'}
        className="max-w-[640px]"
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
          <Field label="Title" value={title} maxLength={RULE_LIMITS.title} onChange={setTitle} />
          <LongField label="Rule" value={text} placeholder="" rows={6} maxLength={RULE_LIMITS.text} onChange={setText} />
        </div>
      </DialogContent>
    </Dialog>
  )
}
