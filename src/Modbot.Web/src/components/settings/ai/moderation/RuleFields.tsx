import { Input } from '@/components/ui/input'
import { TARGETS, type RuleAction } from '@/lib/aiModeration'
import { Checkbox, Switch } from '../../fields'

/** A small labelled group, so the dialogs read as sections without a paragraph of text. */
export function Group({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
      <legend className="mb-1 text-muted-foreground">{label}</legend>
      {children}
    </fieldset>
  )
}

/**
 * The part every rule shares: on or off, what it looks at, and what it does.
 *
 * Delete and time out only apply to Discord messages, so they are disabled until Discord messages
 * is ticked -- the server refuses the combination anyway, and a disabled box says so without words.
 */
export function RuleActionFields({
  value,
  onChange,
}: {
  value: RuleAction
  onChange: (next: RuleAction) => void
}) {
  const chat = value.targets.includes('discordMessage')

  const toggleTarget = (target: RuleAction['targets'][number], on: boolean) => {
    const targets = on ? [...value.targets, target] : value.targets.filter((t) => t !== target)
    const keepsChat = targets.includes('discordMessage')
    onChange({
      ...value,
      targets,
      deleteMessage: keepsChat && value.deleteMessage,
      timeoutMinutes: keepsChat ? value.timeoutMinutes : null,
    })
  }

  return (
    <>
      <Switch checked={value.enabled} onChange={(enabled) => onChange({ ...value, enabled })}>
        On
      </Switch>

      <Group label="Checks">
        <div className="flex flex-wrap gap-x-4 gap-y-1.5">
          {TARGETS.map((t) => (
            <Checkbox
              key={t.value}
              checked={value.targets.includes(t.value)}
              onChange={(on) => toggleTarget(t.value, on)}
            >
              {t.label}
            </Checkbox>
          ))}
        </div>
      </Group>

      <Group label="Action">
        <Checkbox
          checked={value.deleteMessage}
          disabled={!chat}
          onChange={(deleteMessage) => onChange({ ...value, deleteMessage })}
        >
          Delete the message
        </Checkbox>
        <div className="flex items-center gap-2">
          <Checkbox
            checked={value.timeoutMinutes !== null}
            disabled={!chat}
            onChange={(on) =>
              onChange({
                ...value,
                timeoutMinutes: on ? (value.timeoutMinutes ?? 60) : null,
              })
            }
          >
            Time out for
          </Checkbox>
          <Input
            type="number"
            min={1}
            max={40320}
            className="h-7 w-24"
            aria-label="Timeout minutes"
            disabled={!chat || value.timeoutMinutes === null}
            value={value.timeoutMinutes ?? ''}
            onChange={(e) => {
              const minutes = Number.parseInt(e.target.value, 10)
              onChange({
                ...value,
                timeoutMinutes: Number.isNaN(minutes) ? 1 : minutes,
              })
            }}
          />
          <span className="text-muted-foreground">minutes</span>
        </div>
      </Group>
    </>
  )
}
