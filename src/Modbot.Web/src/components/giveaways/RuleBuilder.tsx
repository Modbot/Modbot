import { X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import {
  COMBINE_LABEL,
  COMBINING,
  RULE_LABEL,
  RULE_UNIT,
  isCombining,
  takesAmount,
  takesRole,
  takesWindow,
  type GiveawayBuilder,
  type GiveawayRule,
} from '@/lib/giveaways'

/**
 * The rule builder: controls, not a query language (M7 §2.1).
 *
 * The audience is a community manager, so every rule is a row of pickers and boxes and the only
 * structure on screen is the group it sits in. Nesting is allowed but never required: a flat list
 * inside "All of" is what almost every giveaway wants, and it is what the builder opens on.
 */
export function RuleBuilder({
  rule,
  builder,
  onChange,
}: {
  rule: GiveawayRule
  builder: GiveawayBuilder | null
  onChange: (rule: GiveawayRule) => void
}) {
  return <Group rule={rule} builder={builder} depth={1} onChange={onChange} />
}

function Group({
  rule,
  builder,
  depth,
  onChange,
  onRemove,
}: {
  rule: GiveawayRule
  builder: GiveawayBuilder | null
  depth: number
  onChange: (rule: GiveawayRule) => void
  onRemove?: () => void
}) {
  const rules = rule.rules ?? []
  const kinds = builder?.ruleKinds ?? Object.keys(RULE_LABEL)

  const replace = (index: number, next: GiveawayRule) =>
    onChange({ ...rule, rules: rules.map((r, i) => (i === index ? next : r)) })

  const remove = (index: number) => onChange({ ...rule, rules: rules.filter((_, i) => i !== index) })

  return (
    <div
      className="flex flex-col gap-2 rounded-md border p-3"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="flex items-center gap-2">
        <Select value={rule.kind} onChange={(kind) => onChange({ ...rule, kind })} className="h-9" aria-label="How the rules combine">
          {COMBINING.map((k) => (
            <option key={k} value={k}>
              {COMBINE_LABEL[k]}
            </option>
          ))}
        </Select>

        <div className="flex-1" />

        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onChange({ ...rule, rules: [...rules, { kind: kinds[0] ?? 'inGroup', amount: 1 }] })}
        >
          Add rule
        </Button>

        {depth < 3 && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => onChange({ ...rule, rules: [...rules, { kind: 'anyOf', rules: [] }] })}
          >
            Add group
          </Button>
        )}

        {onRemove && (
          <Button type="button" variant="ghost" size="icon-sm" aria-label="Remove group" onClick={onRemove}>
            <X />
          </Button>
        )}
      </div>

      {rules.map((inner, index) =>
        isCombining(inner.kind) ? (
          <Group
            key={index}
            rule={inner}
            builder={builder}
            depth={depth + 1}
            onChange={(next) => replace(index, next)}
            onRemove={() => remove(index)}
          />
        ) : (
          <Row
            key={index}
            rule={inner}
            builder={builder}
            onChange={(next) => replace(index, next)}
            onRemove={() => remove(index)}
          />
        ),
      )}
    </div>
  )
}

function Row({
  rule,
  builder,
  onChange,
  onRemove,
}: {
  rule: GiveawayRule
  builder: GiveawayBuilder | null
  onChange: (rule: GiveawayRule) => void
  onRemove: () => void
}) {
  const kinds = builder?.ruleKinds ?? Object.keys(RULE_LABEL)
  const roles = rule.kind === 'groupRole' ? (builder?.groupRoles ?? []) : (builder?.discordRoles ?? [])

  return (
    <div className="flex flex-wrap items-center gap-2 rounded-md bg-secondary/50 px-2 py-1.5">
      <Select
        className="h-9"
        aria-label="Rule"
        value={rule.kind}
        onChange={(kind) =>
          onChange({
            kind,
            amount: takesAmount(kind) ? (rule.amount ?? 1) : undefined,
            withinDays: takesWindow(kind) ? (rule.withinDays ?? null) : undefined,
            id: takesRole(kind) ? rule.id : undefined,
          })
        }
      >
        {kinds.map((k) => (
          <option key={k} value={k}>
            {RULE_LABEL[k] ?? k}
          </option>
        ))}
      </Select>

      {takesAmount(rule.kind) && (
        <>
          <Input
            type="number"
            min={0}
            className="h-9 max-w-24"
            value={rule.amount ?? 0}
            onChange={(e) => onChange({ ...rule, amount: Number(e.target.value) })}
          />
          <span className="text-muted-foreground">{RULE_UNIT[rule.kind]}</span>
        </>
      )}

      {takesRole(rule.kind) && (
        <Select
          className="h-9"
          aria-label="Role"
          value={rule.id ?? ''}
          onChange={(id) => onChange({ ...rule, id: id || undefined })}
        >
          <option value="">Pick a role</option>
          {roles.map((r) => (
            <option key={r.id} value={r.id}>
              {r.name}
            </option>
          ))}
        </Select>
      )}

      {takesWindow(rule.kind) && (
        <>
          <Select
            className="h-9"
            aria-label="When it counts"
            value={rule.withinDays == null ? 'all' : 'window'}
            onChange={(choice) => onChange({ ...rule, withinDays: choice === 'all' ? null : (rule.withinDays ?? 30) })}
          >
            <option value="all">All time</option>
            <option value="window">In the last</option>
          </Select>

          {rule.withinDays != null && (
            <>
              <Input
                type="number"
                min={1}
                max={3650}
                className="h-9 max-w-24"
                value={rule.withinDays}
                onChange={(e) => onChange({ ...rule, withinDays: Number(e.target.value) })}
              />
              <span className="text-muted-foreground">days</span>
            </>
          )}
        </>
      )}

      <div className="flex-1" />

      <Button type="button" variant="ghost" size="icon-sm" aria-label="Remove rule" onClick={onRemove}>
        <X />
      </Button>
    </div>
  )
}
