import * as React from 'react'
import { Popover } from 'radix-ui'
import { Check, ChevronDown } from 'lucide-react'
import { cn } from '@/lib/utils'
import { matchTyped, moveActive, readChoices, type Choice } from '@/lib/dropdown'

/**
 * The app's dropdown.
 *
 * The native `<select>` this replaces drew the operating system's own list -- on Windows, a white
 * popup with a blue highlight -- in the middle of a dark app. Almost every moderator is on
 * Windows, so that was the normal case rather than an edge, and nothing the app could theme.
 *
 * It keeps the native control's shape on purpose: `value`, `onChange`, and `<option>` elements as
 * children. Every screen that had a dropdown kept the markup it already had, and a caller does
 * not have to turn a list into an array to use it.
 *
 * The list is a Radix popover, the same layer the Discord pickers and the filter bar use. That is
 * what lets it escape a dialog's scrolling body: the list is drawn in a portal at the top of the
 * page rather than inside the card, so nothing clips it, and the popover's focus scope pauses the
 * dialog's own focus trap while it is open. The listbox and its keys are ours -- see `lib/dropdown`.
 */
export function Select({
  value,
  onChange,
  children,
  className,
  disabled,
  ...rest
}: {
  value: string
  onChange: (value: string) => void
  children: React.ReactNode
  className?: string
  disabled?: boolean
  'aria-label'?: string
  'aria-labelledby'?: string
}) {
  const choices = React.useMemo(() => readChoices(children), [children])
  const [open, setOpen] = React.useState(false)
  const [active, setActive] = React.useState(-1)
  const listId = React.useId()
  const typed = React.useRef('')
  const forgetTyped = React.useRef<number | undefined>(undefined)
  const activeRow = React.useRef<HTMLDivElement | null>(null)

  const selected = choices.findIndex((choice) => choice.value === value)
  const current = selected >= 0 ? choices[selected] : null
  const startAt = selected >= 0 ? selected : moveActive(choices, -1, 'down')
  const rowId = (at: number) => `${listId}-${at}`

  // Opening with the cursor already on the selected option is what makes Enter a no-op rather
  // than a change, so a moderator who opens a dropdown by accident cannot lose their setting.
  const show = (at: number) => {
    setActive(at)
    setOpen(true)
  }

  const choose = (at: number) => {
    const choice = choices[at]
    setOpen(false)
    if (!choice || choice.disabled) return
    // Choosing what is already chosen is not a change, the same as the native control.
    if (choice.value !== value) onChange(choice.value)
  }

  const typeToJump = (letter: string) => {
    window.clearTimeout(forgetTyped.current)
    typed.current += letter
    forgetTyped.current = window.setTimeout(() => {
      typed.current = ''
    }, 600)
    return matchTyped(choices, typed.current, active)
  }

  React.useEffect(() => () => window.clearTimeout(forgetTyped.current), [])

  // Keeps the cursor on screen as it moves, and puts the selected option in view when the list
  // opens -- a list of time zones is otherwise opened scrolled to the top, nowhere near the value.
  // The list is scrolled by hand rather than with scrollIntoView, which would also scroll the page
  // behind it while the popover is still being placed.
  React.useLayoutEffect(() => {
    if (!open) return

    const align = () => {
      const row = activeRow.current
      const list = row?.parentElement
      if (!row || !list) return

      const rowBox = row.getBoundingClientRect()
      const listBox = list.getBoundingClientRect()
      if (rowBox.top < listBox.top) list.scrollTop -= listBox.top - rowBox.top
      else if (rowBox.bottom > listBox.bottom) list.scrollTop += rowBox.bottom - listBox.bottom
    }

    align()
    // The popover only measures the room it has once it is on the page, so on the frame it opens
    // the list is still its full height and every option counts as in view. Aligning again on the
    // next frame is what actually puts the selected time zone in front of the moderator.
    const frame = requestAnimationFrame(align)
    return () => cancelAnimationFrame(frame)
  }, [open, active])

  const onTriggerKeyDown = (event: React.KeyboardEvent) => {
    if (open) return

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      show(selected >= 0 ? selected : moveActive(choices, -1, event.key === 'ArrowDown' ? 'down' : 'up'))
      return
    }

    if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault()
      show(moveActive(choices, -1, event.key === 'Home' ? 'first' : 'last'))
      return
    }

    // Enter and Space are left alone: the browser turns them into a click on the button, and the
    // popover's trigger opens on that. Handling them here as well would open and close again.
    if (event.key === ' ') return

    if (isLetter(event)) {
      event.preventDefault()
      typed.current = ''
      show(matchTyped(choices, event.key.toLowerCase(), selected))
    }
  }

  const onListKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Home' || event.key === 'End') {
      event.preventDefault()
      setActive(
        moveActive(
          choices,
          active,
          event.key === 'ArrowDown' ? 'down' : event.key === 'ArrowUp' ? 'up' : event.key === 'Home' ? 'first' : 'last',
        ),
      )
      return
    }

    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      choose(active)
      return
    }

    if (event.key === 'Tab') {
      // Close and leave the value alone, then let focus go back to the trigger, so Tab out of an
      // open dropdown lands on the next control rather than inside the list.
      event.preventDefault()
      setOpen(false)
      return
    }

    if (isLetter(event)) {
      event.preventDefault()
      setActive(typeToJump(event.key.toLowerCase()))
    }
  }

  return (
    <Popover.Root
      open={open}
      onOpenChange={(next) => {
        if (next) setActive(startAt)
        setOpen(next)
      }}
    >
      <Popover.Trigger asChild>
        <button
          type="button"
          disabled={disabled}
          aria-haspopup="listbox"
          onKeyDown={onTriggerKeyDown}
          className={cn(
            'flex h-8 min-w-0 items-center justify-between gap-1.5 rounded-md border border-input bg-transparent px-2 text-left text-foreground outline-none',
            'text-(length:--text-small) focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50',
            'disabled:cursor-not-allowed disabled:opacity-50',
            className,
          )}
          {...rest}
        >
          <span className="truncate">{current?.label ?? ''}</span>
          <ChevronDown className="size-3.5 shrink-0 opacity-60" aria-hidden />
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          role="listbox"
          tabIndex={-1}
          aria-label={rest['aria-label']}
          aria-labelledby={rest['aria-labelledby']}
          aria-activedescendant={active >= 0 ? rowId(active) : undefined}
          align="start"
          sideOffset={4}
          collisionPadding={8}
          onKeyDown={onListKeyDown}
          className={cn(
            'z-50 overflow-y-auto rounded-xl border bg-popover p-1 text-(length:--text-small) text-popover-foreground shadow-md outline-none',
            // Never taller than the room the popover has, and never taller than a screenful of
            // rows either: a list of time zones has to scroll, not become the page.
            'max-h-[min(20rem,var(--radix-popover-content-available-height))]',
            'min-w-(--radix-popover-trigger-width) max-w-[min(24rem,var(--radix-popover-content-available-width))]',
          )}
        >
          {choices.map((choice, at) => (
            <Row
              key={at}
              choice={choice}
              heading={choice.group !== (choices[at - 1]?.group ?? null) ? choice.group : null}
              id={rowId(at)}
              chosen={at === selected}
              under={at === active}
              ref={at === active ? activeRow : undefined}
              onPick={() => choose(at)}
              onOver={() => setActive(at)}
            />
          ))}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}

function Row({
  choice,
  heading,
  id,
  chosen,
  under,
  ref,
  onPick,
  onOver,
}: {
  choice: Choice
  heading: string | null
  id: string
  chosen: boolean
  under: boolean
  ref?: React.Ref<HTMLDivElement>
  onPick: () => void
  onOver: () => void
}) {
  return (
    <>
      {heading && (
        // Skipped by a screen reader rather than read as a row of the list: the options under it
        // already carry their own text, and a listbox may only hold options.
        <div
          role="presentation"
          className="truncate px-2 pt-2 pb-1 text-xs font-medium tracking-wide text-muted-foreground uppercase"
        >
          {heading}
        </div>
      )}
      <div
        ref={ref}
        id={id}
        role="option"
        aria-selected={chosen}
        aria-disabled={choice.disabled || undefined}
        // The mouse moves the same cursor the arrow keys move, so there is only ever one
        // highlighted row and picking with either lands in the same place.
        onClick={choice.disabled ? undefined : onPick}
        onMouseEnter={choice.disabled ? undefined : onOver}
        className={cn(
          'flex min-w-0 cursor-default items-center gap-2 rounded-md px-2 py-1.5',
          under && 'bg-accent text-accent-foreground',
          choice.disabled && 'opacity-50',
        )}
      >
        <Check className={cn('size-3.5 shrink-0', !chosen && 'invisible')} aria-hidden />
        <span className="truncate">{choice.label}</span>
      </div>
    </>
  )
}

/** A key that types a character, rather than one that moves or commits. */
function isLetter(event: React.KeyboardEvent): boolean {
  return event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey
}
