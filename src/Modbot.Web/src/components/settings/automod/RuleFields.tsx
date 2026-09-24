import { Input } from '@/components/ui/input'
import { CONTEXT_CHOICES, TARGETS, type RuleAction } from '@/lib/autoMod'
import { Checkbox, Switch } from '../fields'
import { RuleScopeFields } from './RuleScopeFields'

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
 * Delete and time out only apply to Discord messages, and the group actions only to VRChat profile
 * text, so each is disabled until a target it could act on is ticked -- the server refuses the
 * combination anyway, and a disabled box says so without words.
 */
export function RuleActionFields({
  value,
  onChange,
  acting,
  picturesAvailable,
}: {
  value: RuleAction
  onChange: (next: RuleAction) => void
  /** The rule already acts, so setting an action here does not start a new trial. */
  acting?: boolean
  /** The model in use reads pictures. False makes the picture box unavailable.  */
  picturesAvailable?: boolean
}) {
  const chat = value.targets.includes('discordMessage')
  const profile = value.targets.some((t) => t !== 'discordMessage')
  const acts = value.deleteMessage || value.timeoutMinutes !== null || value.groupBan || value.groupRemove

  const toggleTarget = (target: RuleAction['targets'][number], on: boolean) => {
    const targets = on ? [...value.targets, target] : value.targets.filter((t) => t !== target)
    const keepsChat = targets.includes('discordMessage')
    const keepsProfile = targets.some((t) => t !== 'discordMessage')
    onChange({
      ...value,
      targets,
      deleteMessage: keepsChat && value.deleteMessage,
      timeoutMinutes: keepsChat ? value.timeoutMinutes : null,
      groupBan: keepsProfile && value.groupBan,
      groupRemove: keepsProfile && value.groupRemove,
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

      <Group label="Earlier messages">
        <div role="radiogroup" aria-label="Earlier messages" className="flex flex-wrap gap-4">
          {CONTEXT_CHOICES.map((n) => (
            <label key={n} className="flex items-center gap-2">
              <input
                type="radio"
                name="contextMessages"
                checked={value.contextMessages === n}
                disabled={!chat}
                onChange={() => onChange({ ...value, contextMessages: n })}
              />
              {n === 0 ? 'None' : `${n} messages`}
            </label>
          ))}
        </div>
      </Group>

      <Group label="Pictures">
        <Checkbox
          checked={value.checkPictures && picturesAvailable !== false}
          disabled={picturesAvailable === false}
          onChange={(checkPictures) => onChange({ ...value, checkPictures })}
        >
          {picturesAvailable === false ? 'Check pictures (the model reads none)' : 'Check pictures'}
        </Checkbox>
      </Group>

      <Group label="Action">
        <Checkbox checked disabled onChange={() => undefined}>
          Flag
        </Checkbox>
        <Checkbox
          checked={value.openReviewForEachFlag}
          onChange={(openReviewForEachFlag) => onChange({ ...value, openReviewForEachFlag })}
        >
          Send to Reviews
        </Checkbox>
        <Checkbox
          checked={value.deleteMessage}
          disabled={!chat}
          onChange={(deleteMessage) => onChange({ ...value, deleteMessage })}
        >
          Delete the Discord message
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
            className="w-24"
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
        <Checkbox
          checked={value.groupBan}
          disabled={!profile}
          onChange={(groupBan) => onChange({ ...value, groupBan })}
        >
          Ban from the VRChat group
        </Checkbox>
        <Checkbox
          checked={value.groupRemove}
          disabled={!profile}
          onChange={(groupRemove) => onChange({ ...value, groupRemove })}
        >
          Remove from the VRChat group
        </Checkbox>
      </Group>

      {acts && !acting && (
        <Group label="Trial">
          <div className="flex items-center gap-2">
            <Input
              type="number"
              min={1}
              max={90}
              className="w-24"
              aria-label="Trial days"
              value={value.trialDays ?? 7}
              onChange={(e) => {
                const days = Number.parseInt(e.target.value, 10)
                onChange({ ...value, trialDays: Number.isNaN(days) ? 7 : days })
              }}
            />
            <span className="text-muted-foreground">days</span>
          </div>
          <Checkbox
            checked={value.actWithoutTest ?? false}
            onChange={(actWithoutTest) => onChange({ ...value, actWithoutTest })}
          >
            Act without a passing test run
          </Checkbox>
        </Group>
      )}

      <RuleScopeFields value={value.scope} onChange={(scope) => onChange({ ...value, scope })} />
    </>
  )
}
