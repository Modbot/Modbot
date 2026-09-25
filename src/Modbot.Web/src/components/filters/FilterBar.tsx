import { useEffect, useMemo, useRef, useState } from 'react'
import { Popover } from 'radix-ui'
import { Check, ListFilter, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Kbd } from '@/components/ui/kbd'
import { SwitchBank } from '@/components/ui/switch-bank'
import { EmptyRow } from '@/components/PanelGrid'
import {
  operatorsFor,
  operatorWords,
  type FilterChip,
  type FilterOperator,
  type FilterProperty,
} from '@/lib/filters'
import { useShortcuts } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'

/**
 * The filter bar: one chip per property, an add control, and a clear control.
 *
 * Linear's shape (research 2026-09-16): press the button or `f`, pick a property by name, pick
 * values; the chip reads `property · operator · values`, its operator and its values can each be
 * changed in place, and `×` takes it away. Chips combine with AND, the values inside one are
 * "any of", and "is not" is the same list turned around.
 *
 * The bar draws what it is given. Where the chips live -- the address, and the browser's memory
 * of the page -- is `lib/filters.ts`'s business.
 */
export function FilterBar({
  properties,
  chips,
  onChange,
  children,
}: {
  properties: FilterProperty[]
  chips: FilterChip[]
  onChange: (chips: FilterChip[]) => void
  /** Drawn at the right end: a search box, a sort control, a count. */
  children?: React.ReactNode
}) {
  const [adding, setAdding] = useState(false)
  const byId = useMemo(() => new Map(properties.map((p) => [p.id, p])), [properties])

  useShortcuts([
    { keys: 'f', label: 'Add a filter', group: 'Filters', page: true, run: () => setAdding(true) },
    {
      keys: 'shift+f',
      label: 'Remove the last filter',
      group: 'Filters',
      page: true,
      hidden: true,
      run: () => onChange(chips.slice(0, -1)),
    },
  ])

  const replace = (next: FilterChip) =>
    onChange(chips.some((c) => c.property === next.property)
      ? chips.map((c) => (c.property === next.property ? next : c))
      : [...chips, next])

  const remove = (property: string) => onChange(chips.filter((c) => c.property !== property))

  return (
    <div className="flex flex-wrap items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
      {chips.map((chip) => {
        const property = byId.get(chip.property)
        if (!property) return null
        return <Chip key={chip.property} property={property} chip={chip} onChange={replace} onRemove={() => remove(chip.property)} />
      })}

      <AddFilter
        open={adding}
        onOpenChange={setAdding}
        properties={properties.filter((p) => !chips.some((c) => c.property === p.id))}
        onAdd={replace}
      />

      {chips.length > 0 && (
        <Button variant="ghost" onClick={() => onChange([])}>
          Clear
        </Button>
      )}

      {/* The right end, as one item rather than a spacer and a row of loose ones. A bare
          `flex-1` spacer between them is a flex item of its own: when the bar wraps it can land
          at the start of a line and grow to the whole width, leaving a blank row with the search
          box indented after it and Clear stranded above. Wrapped together, the group either
          shares the line and is pushed right, or takes a line of its own and starts at the left
          edge like everything else. */}
      <div className="flex flex-1 flex-wrap items-center gap-2 md:justify-end">{children}</div>
    </div>
  )
}

// ── One chip ────────────────────────────────────────────────────────────────────────────────

function Chip({
  property,
  chip,
  onChange,
  onRemove,
}: {
  property: FilterProperty
  chip: FilterChip
  onChange: (chip: FilterChip) => void
  onRemove: () => void
}) {
  const operators = operatorsFor(property)
  const [editing, setEditing] = useState(false)

  const labels = chip.values.map((v) => property.options?.find((o) => o.value === v)?.label ?? v)
  const shown = labels.length > 3 ? `${labels.slice(0, 3).join(', ')} +${labels.length - 3}` : labels.join(', ')

  const nextOperator = () => {
    const at = operators.indexOf(chip.operator)
    const next = operators[(at + 1) % operators.length]
    onChange({ ...chip, operator: next })
  }

  return (
    <span className="inline-flex h-(--control-h) items-stretch divide-x-(--hairline) divide-border overflow-hidden rounded-sm border border-(length:--hairline) bg-card">
      <span className="flex items-center px-2 text-muted-foreground">{property.label}</span>

      {operators.length > 1 && property.kind !== 'date' ? (
        <button
          type="button"
          onClick={nextOperator}
          className="px-2 text-muted-foreground hover:bg-muted hover:text-foreground"
          aria-label={`${property.label}: ${operatorWords(chip.operator, chip.values.length)}`}
        >
          {operatorWords(chip.operator, chip.values.length)}
        </button>
      ) : (
        <span className="flex items-center px-2 text-muted-foreground">
          {operatorWords(chip.operator, chip.values.length)}
        </span>
      )}

      {property.kind !== 'yesno' && (
        <Popover.Root open={editing} onOpenChange={setEditing}>
          <Popover.Trigger asChild>
            <button
              type="button"
              className="max-w-[20rem] truncate px-2 font-medium hover:bg-muted"
            >
              {shown || '…'}
            </button>
          </Popover.Trigger>
          <Popover.Portal>
            <Popover.Content align="start" sideOffset={4} className="z-50 outline-none">
              <ValueEditor property={property} chip={chip} onChange={onChange} onDone={() => setEditing(false)} />
            </Popover.Content>
          </Popover.Portal>
        </Popover.Root>
      )}

      <button
        type="button"
        onClick={onRemove}
        aria-label={`Remove the ${property.label} filter`}
        className="px-1.5 text-muted-foreground hover:bg-muted hover:text-foreground"
      >
        <X className="size-3.5" />
      </button>
    </span>
  )
}

// ── Adding one ──────────────────────────────────────────────────────────────────────────────

function AddFilter({
  open,
  onOpenChange,
  properties,
  onAdd,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  properties: FilterProperty[]
  onAdd: (chip: FilterChip) => void
}) {
  return (
    <Popover.Root open={open} onOpenChange={onOpenChange}>
      <Popover.Trigger asChild>
        <Button variant="outline">
          <ListFilter className="size-3.5" />
          Filter
          <Kbd keys="f" />
        </Button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={4} className="z-50 outline-none">
          {/* Mounted with the popover, so every opening starts at the property list with nothing typed. */}
          <AddPanel properties={properties} onAdd={onAdd} close={() => onOpenChange(false)} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}

function AddPanel({
  properties,
  onAdd,
  close,
}: {
  properties: FilterProperty[]
  onAdd: (chip: FilterChip) => void
  close: () => void
}) {
  const [picked, setPicked] = useState<FilterProperty | null>(null)
  const [typed, setTyped] = useState('')
  const [cursor, setCursor] = useState(0)

  const words = typed.trim().toLowerCase()
  const matching = properties.filter((p) => p.label.toLowerCase().includes(words))
  const at = Math.min(cursor, Math.max(0, matching.length - 1))

  if (picked) {
    return (
      <ValueEditor
        property={picked}
        chip={null}
        onChange={(chip) => {
          onAdd(chip)
          if (picked.kind !== 'choice') close()
        }}
        onDone={close}
      />
    )
  }

  return (
    <Panel>
      <input
        autoFocus
        value={typed}
        onChange={(e) => {
          setTyped(e.target.value)
          setCursor(0)
        }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            setCursor((c) => Math.min(matching.length - 1, c + 1))
          } else if (e.key === 'ArrowUp') {
            e.preventDefault()
            setCursor((c) => Math.max(0, c - 1))
          } else if (e.key === 'Enter' && matching[at]) {
            e.preventDefault()
            setPicked(matching[at])
          }
        }}
        placeholder="Filter by"
        aria-label="Filter by"
        className="h-(--control-h) w-full border-b border-b-(length:--hairline) bg-transparent px-2 outline-none placeholder:text-muted-foreground"
      />
      <div className="max-h-72 overflow-auto py-1">
        {matching.length === 0 && <EmptyRow className="px-2">Nothing matches</EmptyRow>}
        {matching.map((p, i) => (
          <Row key={p.id} active={i === at} onClick={() => setPicked(p)} onHover={() => setCursor(i)}>
            {p.label}
          </Row>
        ))}
      </div>
    </Panel>
  )
}

// ── Choosing values ─────────────────────────────────────────────────────────────────────────

/**
 * The editor for one property's values: a list with ticks for a choice, a box for an id or a
 * word, two days for a date, two buttons for yes or no. Every change is applied at once, the
 * way Linear's pickers work; `onDone` closes the editor when the kind has only one answer.
 */
function ValueEditor({
  property,
  chip,
  onChange,
  onDone,
}: {
  property: FilterProperty
  chip: FilterChip | null
  onChange: (chip: FilterChip) => void
  onDone: () => void
}) {
  const operators = operatorsFor(property)
  const operator = chip?.operator ?? operators[0]
  const values = chip?.values ?? []

  const [typed, setTyped] = useState(property.kind === 'id' || property.kind === 'text' ? (values[0] ?? '') : '')
  const [cursor, setCursor] = useState(0)

  if (property.kind === 'yesno') {
    return (
      <Panel>
        {(['yes', 'no'] as const).map((answer) => (
          <Row
            key={answer}
            active={operator === answer}
            onClick={() => {
              onChange({ property: property.id, operator: answer, values: [] })
              onDone()
            }}
          >
            {answer === 'yes' ? 'Yes' : 'No'}
          </Row>
        ))}
      </Panel>
    )
  }

  if (property.kind === 'id' || property.kind === 'text') {
    const apply = () => {
      const value = typed.trim()
      if (!value) return
      onChange({ property: property.id, operator: operators[0], values: [value] })
      onDone()
    }

    return (
      <Panel>
        <div className="flex items-center gap-1 p-1">
          <Input
            autoFocus
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault()
                apply()
              }
            }}
            placeholder={property.placeholder}
            aria-label={property.label}
            className="w-64"
          />
          <Button onClick={apply}>
            Apply
          </Button>
        </div>
      </Panel>
    )
  }

  if (property.kind === 'date') {
    const from = operator === 'before' ? '' : (values[0] ?? '')
    const to = operator === 'before' ? (values[0] ?? '') : operator === 'between' ? (values[1] ?? '') : ''

    const set = (nextFrom: string, nextTo: string) => {
      if (nextFrom && nextTo) onChange({ property: property.id, operator: 'between', values: [nextFrom, nextTo] })
      else if (nextFrom) onChange({ property: property.id, operator: 'after', values: [nextFrom] })
      else if (nextTo) onChange({ property: property.id, operator: 'before', values: [nextTo] })
    }

    return (
      <Panel>
        <div className="grid grid-cols-[auto_1fr] items-center gap-x-2 gap-y-1 p-2">
          <span className="text-muted-foreground">From</span>
          <Input type="date" value={from} onChange={(e) => set(e.target.value, to)} aria-label={`${property.label} from`} autoFocus />
          <span className="text-muted-foreground">To</span>
          <Input type="date" value={to} onChange={(e) => set(from, e.target.value)} aria-label={`${property.label} to`} />
        </div>
      </Panel>
    )
  }

  // A choice: tick values from the list; type to narrow it, or to name a value it does not carry.
  const options = property.options ?? []
  const words = typed.trim().toLowerCase()
  const matching = options.filter((o) => o.label.toLowerCase().includes(words) || o.value.toLowerCase().includes(words))
  const custom = property.freeText && typed.trim() && !options.some((o) => o.value === typed.trim()) ? typed.trim() : null
  const rows: { value: string; label: string; count?: number | null; color?: string | null }[] = [
    ...matching,
    ...(custom ? [{ value: custom, label: custom }] : []),
  ]
  const at = Math.min(cursor, Math.max(0, rows.length - 1))

  const toggle = (value: string) => {
    const has = values.includes(value)
    const next = property.multi === false ? (has ? [] : [value]) : has ? values.filter((v) => v !== value) : [...values, value]
    onChange({ property: property.id, operator, values: next })
    if (property.multi === false) onDone()
  }

  const setOperator = (next: FilterOperator) => onChange({ property: property.id, operator: next, values })

  return (
    <Panel>
      {operators.length > 1 && (
        <div className="border-b border-b-(length:--hairline) bg-strip p-1">
          <SwitchBank
            size="sm"
            value={operator}
            onChange={setOperator}
            options={operators.map((o) => ({ value: o, label: operatorWords(o, 2) }))}
          />
        </div>
      )}

      {(options.length > 7 || property.freeText) && (
        <input
          autoFocus
          value={typed}
          onChange={(e) => {
            setTyped(e.target.value)
            setCursor(0)
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault()
              setCursor((c) => Math.min(rows.length - 1, c + 1))
            } else if (e.key === 'ArrowUp') {
              e.preventDefault()
              setCursor((c) => Math.max(0, c - 1))
            } else if (e.key === 'Enter' && rows[at]) {
              e.preventDefault()
              toggle(rows[at].value)
            }
          }}
          placeholder={property.placeholder ?? property.label}
          aria-label={property.label}
          className="h-(--control-h) w-full border-b border-b-(length:--hairline) bg-transparent px-2 outline-none placeholder:text-muted-foreground"
        />
      )}

      <div className="max-h-72 overflow-auto py-1">
        {rows.length === 0 && <EmptyRow className="px-2">Nothing matches</EmptyRow>}
        {rows.map((o, i) => {
          const on = values.includes(o.value)
          return (
            <Row key={o.value} active={i === at} onClick={() => toggle(o.value)} onHover={() => setCursor(i)} role="menuitemcheckbox" checked={on}>
              <span className={cn('flex size-4 shrink-0 items-center justify-center rounded-sm border border-(length:--hairline)', on && 'border-transparent bg-primary text-primary-foreground')}>
                {on && <Check className="size-3" />}
              </span>
              {o.color && <span aria-hidden className="size-2 shrink-0" style={{ background: o.color }} />}
              <span className="min-w-0 flex-1 truncate">{o.label}</span>
              {typeof o.count === 'number' && <span className="font-mono text-muted-foreground">{o.count.toLocaleString()}</span>}
            </Row>
          )
        })}
      </div>
    </Panel>
  )
}

// ── Pieces ──────────────────────────────────────────────────────────────────────────────────

function Panel({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="w-72 overflow-hidden rounded-sm border border-(length:--hairline) bg-popover text-popover-foreground shadow-sm"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {children}
    </div>
  )
}

function Row({
  active,
  onClick,
  onHover,
  role,
  checked,
  children,
}: {
  active: boolean
  onClick: () => void
  onHover?: () => void
  role?: string
  checked?: boolean
  children: React.ReactNode
}) {
  const ref = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (active) ref.current?.scrollIntoView({ block: 'nearest' })
  }, [active])

  return (
    <button
      ref={ref}
      type="button"
      role={role}
      aria-checked={checked}
      onClick={onClick}
      onMouseEnter={onHover}
      className={cn(
        'flex min-h-(--control-h) w-full items-center gap-2 px-2 py-1 text-left',
        active ? 'bg-accent text-accent-foreground' : 'hover:bg-muted',
      )}
    >
      {children}
    </button>
  )
}
