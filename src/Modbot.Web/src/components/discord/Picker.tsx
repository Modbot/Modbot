import { useId, useState } from 'react'
import { Popover } from 'radix-ui'
import { ChevronsUpDown } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

/** A short state beside a name: a missing permission is a problem, "Removed" is just a fact. */
export type PickerMark = { text: string; tone: 'problem' | 'quiet' }

export type PickerOption = {
  id: string
  label: string
  /** Searched as well as the label, so an id pasted into the box finds its channel. */
  keywords?: string
  marks: PickerMark[]
  /** A colour square before the label, as `#rrggbb`. */
  dot?: string | null
}

export type PickerGroup = { heading: string | null; options: PickerOption[] }

/**
 * The searchable select both Discord pickers are built on.
 *
 * A Radix popover rather than a native select: a native one can neither search nor show a
 * missing permission beside each channel, and those two are the reason the picker exists.
 *
 * An id typed into the search box that matches nothing can still be used. Before the bot has
 * connected the lists are empty, and the setting must not become impossible to fill in.
 */
export function Picker({
  label,
  value,
  onChange,
  groups,
  current,
  allowNone,
  error,
  disabled,
  onOpen,
}: {
  label: string
  value: string
  onChange: (id: string) => void
  groups: PickerGroup[]
  /** How the saved value reads on the closed control. */
  current: { label: string; marks: PickerMark[]; dot?: string | null } | null
  allowNone: boolean
  error: string | null
  disabled?: boolean
  onOpen?: () => void
}) {
  const labelId = useId()
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')

  const needle = query.trim().toLowerCase()
  const visible = groups
    .map((g) => ({
      ...g,
      options: g.options.filter(
        (o) =>
          !needle ||
          o.label.toLowerCase().includes(needle) ||
          (o.keywords ?? '').toLowerCase().includes(needle) ||
          o.id === needle,
      ),
    }))
    .filter((g) => g.options.length > 0)

  const typedId = /^\d+$/.test(needle) && !groups.some((g) => g.options.some((o) => o.id === needle)) ? needle : null
  const first = visible[0]?.options[0]?.id ?? typedId

  const choose = (id: string) => {
    onChange(id)
    setOpen(false)
  }

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span id={labelId} className="text-muted-foreground">
        {label}
      </span>
      <Popover.Root
        open={open}
        onOpenChange={(next) => {
          setOpen(next)
          if (next) {
            setQuery('')
            onOpen?.()
          }
        }}
      >
        <Popover.Trigger asChild>
          <button
            type="button"
            aria-labelledby={labelId}
            disabled={disabled}
            className={cn(
              'flex h-(--control-h) w-full min-w-0 items-center justify-between gap-2 rounded-sm border border-(length:--hairline) border-input bg-card px-2 text-left text-(length:--text-small) text-foreground outline-none',
              'focus-visible:border-ring focus-visible:ring-1 focus-visible:ring-ring',
              'disabled:cursor-not-allowed disabled:opacity-50',
            )}
          >
            <span className="flex min-w-0 items-center gap-2">
              {current ? (
                <>
                  <Dot color={current.dot} />
                  <span className="truncate">{current.label}</span>
                  <Marks marks={current.marks} />
                </>
              ) : (
                <span className="text-muted-foreground">None</span>
              )}
            </span>
            <ChevronsUpDown className="size-3.5 shrink-0 opacity-60" />
          </button>
        </Popover.Trigger>
        <Popover.Portal>
          <Popover.Content
            align="start"
            sideOffset={4}
            className="z-50 w-(--radix-popover-trigger-width) min-w-[18rem] rounded-sm border border-(length:--hairline) bg-popover py-1 text-(length:--text-small) text-popover-foreground shadow-sm"
          >
            <div className="px-1">
              <Input
                autoFocus
                value={query}
                placeholder="Search"
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    if (first) choose(first)
                  }
                }}
              />
            </div>
            <div role="listbox" aria-labelledby={labelId} className="mt-1 max-h-72 overflow-y-auto">
              {error && <div className="px-2 py-1.5 text-destructive">{error}</div>}
              {allowNone && !needle && (
                <Option selected={!value} onSelect={() => choose('')}>
                  <span className="text-muted-foreground">None</span>
                </Option>
              )}
              {visible.map((group, index) => (
                <div key={group.heading ?? `group-${index}`}>
                  {group.heading && (
                    <div className="truncate px-2 pt-2 pb-1 font-label text-muted-foreground">
                      {group.heading}
                    </div>
                  )}
                  {group.options.map((option) => (
                    <Option key={option.id} selected={option.id === value} onSelect={() => choose(option.id)}>
                      <Dot color={option.dot} />
                      <span className="truncate">{option.label}</span>
                      <Marks marks={option.marks} />
                    </Option>
                  ))}
                </div>
              ))}
              {typedId && (
                <Option selected={false} onSelect={() => choose(typedId)}>
                  <span className="truncate">Use {typedId}</span>
                </Option>
              )}
              {!visible.length && !typedId && !error && (needle || !allowNone) && (
                <div className="px-2 py-1.5 text-muted-foreground">No matches</div>
              )}
            </div>
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
    </div>
  )
}

function Option({
  selected,
  onSelect,
  children,
}: {
  selected: boolean
  onSelect: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      role="option"
      aria-selected={selected}
      onClick={onSelect}
      className={cn(
        'flex w-full min-w-0 items-center gap-2 px-2 py-1.5 text-left outline-none',
        'hover:bg-accent hover:text-accent-foreground focus-visible:bg-accent focus-visible:text-accent-foreground',
        selected && 'bg-muted font-medium',
      )}
    >
      {children}
    </button>
  )
}

function Marks({ marks }: { marks: PickerMark[] }) {
  return (
    <>
      {marks.map((mark) => (
        <span
          key={mark.text}
          className={cn('ml-auto shrink-0 text-xs', mark.tone === 'problem' ? 'text-warn' : 'text-muted-foreground')}
        >
          {mark.text}
        </span>
      ))}
    </>
  )
}

function Dot({ color }: { color?: string | null }) {
  if (color === undefined) return null

  return (
    <span
      aria-hidden
      className="inline-block size-2 shrink-0 border border-border"
      style={color ? { backgroundColor: color, borderColor: color } : undefined}
    />
  )
}
