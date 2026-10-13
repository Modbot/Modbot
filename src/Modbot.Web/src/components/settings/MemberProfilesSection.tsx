import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { EmptyRow } from '@/components/PanelGrid'
import { RolePicker } from '@/components/discord/RolePicker'
import {
  api,
  ApiError,
  type ProfileFieldKind,
  type ProfileFieldView,
  type ProfileVisibility,
} from '@/lib/api'
import { cn } from '@/lib/utils'
import { Outcome } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'

// The server's limits (ProfileField.MaxFields and the others), repeated so a box stops typing at
// the same place the server would refuse.
const MAX_FIELDS = 25
const MAX_OPTIONS = 25
const MAX_NAME = 40
const MAX_OPTION = 60

const LEVELS: { value: ProfileVisibility; label: string }[] = [
  { value: 'only-me', label: 'Only me' },
  { value: 'staff', label: 'Staff' },
  { value: 'members', label: 'Members' },
]

/** One option being edited: the id it already has on the server, or null for a new one; roleId is '' for none. */
type OptionDraft = { id: string | null; label: string; roleId: string; key: number }

let nextKey = 0
const draftsOf = (field: ProfileFieldView): OptionDraft[] =>
  field.options.map((o) => ({ id: o.id, label: o.label, roleId: o.roleId ?? '', key: nextKey++ }))
const blankOption = (): OptionDraft => ({ id: null, label: '', roleId: '', key: nextKey++ })
const sendable = (options: OptionDraft[], keepIds: boolean) =>
  options
    .filter((o) => o.label.trim())
    .map((o) => ({ id: keepIds ? o.id : null, label: o.label.trim(), roleId: o.roleId || null }))

/** Settings → Member profiles: the fields every member can fill in with /profile in Discord. */
export function MemberProfilesSection() {
  return (
    <SettingsSection id="member-profiles" title="Member profiles">
      <ProfileFieldsCard />
    </SettingsSection>
  )
}

function ProfileFieldsCard() {
  const [fields, setFields] = useState<ProfileFieldView[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [copied, setCopied] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState<string | null>(null)
  const [removing, setRemoving] = useState<ProfileFieldView | null>(null)

  const load = useCallback(
    () =>
      api
        .profileFields()
        .then((list) => {
          setFields(list.fields)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to see the profile fields.'
              : 'Could not load the profile fields.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  /** Runs a change, reloads the list, and says why when it fails. Resolves true when it worked. */
  const run = (work: Promise<unknown>): Promise<boolean> => {
    setBusy(true)
    setProblem(null)

    return work
      .then(() => load())
      .then(() => true)
      .catch((e: unknown) => {
        setProblem(e instanceof ApiError ? e.message : 'Could not save the change.')
        return false
      })
      .finally(() => setBusy(false))
  }

  const copyFromRoles = (field: ProfileFieldView) => {
    setBusy(true)
    setProblem(null)
    setCopied(null)

    api
      .copyProfileFieldFromRoles(field.id)
      .then((result) => setCopied(`${field.name}: ${result.filled} filled`))
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not copy the roles.'))
      .finally(() => setBusy(false))
  }

  const move = (index: number, by: -1 | 1) => {
    if (!fields) return
    const next = [...fields]
    const to = index + by
    if (to < 0 || to >= next.length) return
    ;[next[index], next[to]] = [next[to], next[index]]
    void run(api.reorderProfileFields(next.map((f) => f.id)))
  }

  return (
    <SettingsCard
      span={12}
      title="Profile fields"
      flush
      footer={
        <>
          <Button
            size="xs"
            variant="outline"
            disabled={busy || (fields !== null && fields.length >= MAX_FIELDS && !adding)}
            onClick={() => setAdding((open) => !open)}
          >
            {adding ? 'Cancel' : 'Add a field'}
          </Button>
          <Outcome tone="ok">{copied}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {error ? (
        <EmptyRow tone="danger" onTryAgain={load}>{error}</EmptyRow>
      ) : !fields ? (
        <EmptyRow tone="loading" />
      ) : (
        <>
          <ul className="flex flex-col">
            {fields.map((field, index) => (
              <li
                key={field.id}
                className={cn(
                  'flex flex-col border-b border-b-(length:--hairline)',
                  !adding && 'last:border-b-0',
                )}
                style={{ fontSize: 'var(--text-small)' }}
              >
                <div className="flex min-h-(--row-h) flex-wrap items-center gap-x-2 gap-y-1 px-(--panel-pad) py-1">
                  <span className="font-medium">{field.name}</span>
                  <span className="min-w-0 flex-1 truncate text-muted-foreground max-sm:basis-full">
                    {field.kind === 'text' ? 'Text' : `Pick-list, ${field.options.length}`}
                  </span>

                  <span className="text-muted-foreground">
                    {LEVELS.find((l) => l.value === field.defaultVisibility)?.label}
                  </span>

                  <div className="flex flex-wrap items-center justify-end gap-1 max-sm:ml-auto">
                    <Button
                      size="icon-xs"
                      variant="ghost"
                      disabled={busy || index === 0}
                      onClick={() => move(index, -1)}
                      aria-label={`Move ${field.name} up`}
                      title="Move up"
                    >
                      ↑
                    </Button>
                    <Button
                      size="icon-xs"
                      variant="ghost"
                      disabled={busy || index === fields.length - 1}
                      onClick={() => move(index, 1)}
                      aria-label={`Move ${field.name} down`}
                      title="Move down"
                    >
                      ↓
                    </Button>
                    {field.kind === 'pick-list' && (
                      <Button
                        size="xs"
                        variant="ghost"
                        disabled={busy || !field.options.some((o) => o.roleId)}
                        onClick={() => copyFromRoles(field)}
                      >
                        Copy from roles
                      </Button>
                    )}
                    <Button
                      size="xs"
                      variant="ghost"
                      disabled={busy}
                      onClick={() => setEditing((open) => (open === field.id ? null : field.id))}
                    >
                      {editing === field.id ? 'Cancel' : 'Edit'}
                    </Button>
                    <Button size="xs" variant="ghost" disabled={busy} onClick={() => setRemoving(field)}>
                      Remove
                    </Button>
                  </div>
                </div>

                {editing === field.id && (
                  <FieldEditor
                    field={field}
                    busy={busy}
                    onSave={(name, options) =>
                      run(api.updateProfileField(field.id, { name, options })).then((ok) => {
                        if (ok) setEditing(null)
                      })
                    }
                  />
                )}
              </li>
            ))}
          </ul>

          {adding && (
            <NewField
              busy={busy}
              onAdd={(body) =>
                run(api.createProfileField(body)).then((ok) => {
                  if (ok) setAdding(false)
                })
              }
            />
          )}
        </>
      )}

      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(open) => {
          if (!open) setRemoving(null)
        }}
        title={removing ? `Remove ${removing.name}?` : 'Remove field?'}
        action="Remove"
        failed="Could not remove the field."
        onConfirm={() => api.deleteProfileField(removing!.id)}
        onDone={() => void load()}
      />
    </SettingsCard>
  )
}

/** Rename a field and, for a pick-list, change its options. */
function FieldEditor({
  field,
  busy,
  onSave,
}: {
  field: ProfileFieldView
  busy: boolean
  onSave: (name: string, options: ReturnType<typeof sendable> | undefined) => Promise<void>
}) {
  const [name, setName] = useState(field.name)
  const [options, setOptions] = useState<OptionDraft[]>(() => draftsOf(field))
  const pickList = field.kind === 'pick-list'

  return (
    <div className="flex flex-col gap-2 border-t border-t-(length:--hairline) p-(--panel-pad)">
      <Input value={name} maxLength={MAX_NAME} aria-label="Field name" onChange={(e) => setName(e.target.value)} />
      {pickList && <OptionsEditor options={options} onChange={setOptions} />}
      <div>
        <Button
          size="sm"
          disabled={busy || name.trim().length === 0}
          onClick={() =>
            void onSave(
              name.trim(),
              pickList ? sendable(options, true) : undefined,
            )
          }
        >
          Save
        </Button>
      </div>
    </div>
  )
}

function NewField({
  busy,
  onAdd,
}: {
  busy: boolean
  onAdd: (body: Parameters<typeof api.createProfileField>[0]) => Promise<void>
}) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState<ProfileFieldKind>('pick-list')
  const [visibility, setVisibility] = useState<ProfileVisibility>('staff')
  const [options, setOptions] = useState<OptionDraft[]>(() => [blankOption()])

  return (
    <div className="flex flex-col gap-2 p-(--panel-pad)">
      <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_9rem_9rem]">
        <Input value={name} placeholder="Pronouns" maxLength={MAX_NAME} aria-label="Field name" onChange={(e) => setName(e.target.value)} />
        <Select aria-label="Kind" value={kind} onChange={(v) => setKind(v as ProfileFieldKind)}>
          <option value="pick-list">Pick-list</option>
          <option value="text">Text</option>
        </Select>
        <Select aria-label="Who sees it" value={visibility} onChange={(v) => setVisibility(v as ProfileVisibility)}>
          {LEVELS.map((l) => (
            <option key={l.value} value={l.value}>
              {l.label}
            </option>
          ))}
        </Select>
      </div>
      {kind === 'pick-list' && <OptionsEditor options={options} onChange={setOptions} />}
      <div>
        <Button
          size="sm"
          disabled={busy || name.trim().length === 0}
          onClick={() =>
            void onAdd({
              name: name.trim(),
              kind,
              defaultVisibility: visibility,
              options: kind === 'pick-list' ? sendable(options, false) : undefined,
            })
          }
        >
          Add it to the end
        </Button>
      </div>
    </div>
  )
}

function OptionsEditor({
  options,
  onChange,
}: {
  options: OptionDraft[]
  onChange: (next: OptionDraft[]) => void
}) {
  const swap = (index: number, by: -1 | 1) => {
    const to = index + by
    if (to < 0 || to >= options.length) return
    const next = [...options]
    ;[next[index], next[to]] = [next[to], next[index]]
    onChange(next)
  }

  return (
    <ul className="flex flex-col gap-1" aria-label="Options">
      {options.map((option, index) => (
        <li key={option.key} className="grid items-end gap-1 sm:grid-cols-[minmax(0,1fr)_14rem_auto]">
          <Input
            value={option.label}
            maxLength={MAX_OPTION}
            aria-label={`Option ${index + 1}`}
            onChange={(e) => onChange(options.map((o) => (o === option ? { ...o, label: e.target.value } : o)))}
          />
          <RolePicker
            label="Role"
            value={option.roleId}
            onChange={(roleId) => onChange(options.map((o) => (o === option ? { ...o, roleId } : o)))}
          />
          <div className="flex items-center gap-1">
            <Button size="icon-xs" variant="ghost" disabled={index === 0} onClick={() => swap(index, -1)} aria-label={`Move option ${index + 1} up`} title="Move up">
              ↑
            </Button>
            <Button
              size="icon-xs"
              variant="ghost"
              disabled={index === options.length - 1}
              onClick={() => swap(index, 1)}
              aria-label={`Move option ${index + 1} down`}
              title="Move down"
            >
              ↓
            </Button>
            <Button
              size="xs"
              variant="ghost"
              onClick={() => onChange(options.filter((o) => o !== option))}
              aria-label={`Remove option ${index + 1}`}
            >
              Remove
            </Button>
          </div>
        </li>
      ))}
      <li>
        <Button
          size="xs"
          variant="outline"
          disabled={options.length >= MAX_OPTIONS}
          onClick={() => onChange([...options, blankOption()])}
        >
          Add an option
        </Button>
      </li>
    </ul>
  )
}
