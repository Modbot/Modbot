import { useId } from 'react'
import { Pencil, Plus, X } from 'lucide-react'
import { Outcome } from '@/components/settings/fields'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { LIMITS, languageChoices } from '@/lib/groupProfile'
import { languageName } from '@/lib/groupOverview'
import { cn } from '@/lib/utils'

/**
 * The controls for editing the group's VRChat page, shared by the Overview's edit-in-place cards and
 * the Settings tab, and meant for the tabs that follow them (roles, invites, the gallery).
 *
 * The pattern: a card shows a value; an edit button (a pencil, named for what it edits) opens the
 * editor in the card's place; Save sends one request and closes it once the server has said yes;
 * Cancel puts the value back. A refusal keeps the editor open with the server's words, which for a
 * refusal from VRChat are VRChat's own. `useSave` (lib/useSave.ts) runs the Save.
 */

/** The pencil in a card's header strip. `label` names what it edits: "Edit languages". */
export function EditButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <Button variant="ghost" size="icon-xs" aria-label={label} title={label} onClick={onClick}>
      <Pencil />
    </Button>
  )
}

/** Cancel and Save, with the reason a Save failed beside them. */
export function SaveCancel({
  saving,
  disabled,
  problem,
  onSave,
  onCancel,
  save = 'Save',
}: {
  saving: boolean
  /** Nothing to save yet, or something in the form is wrong. */
  disabled?: boolean
  problem: string | null
  onSave: () => void
  onCancel: () => void
  save?: string
}) {
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <Button size="sm" variant="outline" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button size="sm" onClick={onSave} disabled={saving || disabled}>
          {saving ? 'Saving…' : save}
        </Button>
      </div>
      <div className="text-right">
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}

/** A labelled box, with the rule it breaks under it when it breaks one. */
export function FieldRow({
  label,
  problem,
  count,
  children,
}: {
  label: string
  problem?: string | null
  /** "12/250" beside the label, for a field with a length limit. */
  count?: string
  children: (id: string) => React.ReactNode
}) {
  const id = useId()

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex items-baseline justify-between gap-2">
        <label htmlFor={id} className="text-muted-foreground">
          {label}
        </label>
        {count && <span className={cn('font-mono', problem ? 'text-destructive' : 'text-muted-foreground')}>{count}</span>}
      </div>
      {children(id)}
      {problem && <span className="text-destructive">{problem}</span>}
    </div>
  )
}

/** A one-line text field for the form. */
export function TextBox({ id, value, onChange, invalid }: { id: string; value: string; onChange: (v: string) => void; invalid?: boolean }) {
  return <Input id={id} value={value} aria-invalid={invalid || undefined} onChange={(e) => onChange(e.target.value)} />
}

/** A text box that grows with what is in it, up to a point, for a description or rules. */
export function LongBox({
  id,
  value,
  onChange,
  rows = 4,
  invalid,
}: {
  id: string
  value: string
  onChange: (v: string) => void
  rows?: number
  invalid?: boolean
}) {
  return (
    <Textarea
      id={id}
      rows={rows}
      value={value}
      aria-invalid={invalid || undefined}
      className="max-h-[50dvh] resize-y"
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

/**
 * The group's languages: the chosen ones, each with a button that takes it off, and a list of
 * VRChat's languages to add another from, until the group has as many as VRChat allows.
 */
export function LanguagePicker({ value, onChange }: { value: string[]; onChange: (codes: string[]) => void }) {
  const full = value.length >= LIMITS.languages

  return (
    <div className="flex flex-col gap-2">
      {value.length > 0 && (
        <ul className="flex flex-wrap gap-1.5">
          {value.map((code) => (
            <li key={code}>
              <Badge variant="secondary" className="gap-1 pr-0.5" title={code}>
                {languageName(code)}
                <button
                  type="button"
                  aria-label={`Remove ${languageName(code)}`}
                  className="inline-flex size-[1.5em] items-center justify-center rounded-xs text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-ring"
                  onClick={() => onChange(value.filter((c) => c !== code))}
                >
                  <X className="size-3" aria-hidden />
                </button>
              </Badge>
            </li>
          ))}
        </ul>
      )}

      <Select
        value=""
        disabled={full}
        aria-label="Add a language"
        className="w-full sm:w-64"
        onChange={(code) => {
          if (code && !full) onChange([...value, code])
        }}
      >
        <option value="">Add a language</option>
        {languageChoices(value).map((l) => (
          <option key={l.code} value={l.code}>
            {l.name}
          </option>
        ))}
      </Select>
    </div>
  )
}

/**
 * The group's links: one box each, a button that takes one away, and one that adds a box until the
 * group has as many as VRChat allows.
 */
export function LinkListEditor({ value, onChange }: { value: string[]; onChange: (links: string[]) => void }) {
  const set = (at: number, text: string) => onChange(value.map((v, i) => (i === at ? text : v)))

  return (
    <div className="flex flex-col gap-2">
      {value.map((link, at) => (
        <div key={at} className="flex items-center gap-2">
          <Input
            aria-label={`Link ${at + 1}`}
            inputMode="url"
            placeholder="https://"
            className="font-mono"
            value={link}
            onChange={(e) => set(at, e.target.value)}
          />
          <Button
            variant="ghost"
            size="icon"
            aria-label={`Remove link ${at + 1}`}
            title="Remove"
            onClick={() => onChange(value.filter((_, i) => i !== at))}
          >
            <X />
          </Button>
        </div>
      ))}

      {value.length < LIMITS.links && (
        <Button variant="outline" size="sm" className="self-start" onClick={() => onChange([...value, ''])}>
          <Plus /> Add a link
        </Button>
      )}
    </div>
  )
}
