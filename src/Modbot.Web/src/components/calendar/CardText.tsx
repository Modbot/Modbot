import { Button } from '@/components/ui/button'
import { around as aroundOf } from '@/lib/calendarOwnText'
import { cn } from '@/lib/utils'
import { FillInText } from './FillInText'

/** One text of a service's card, as the form edits it. */
export type CardField = {
  value: string
  onChange: (value: string) => void
  /** Hands the form the way to put a fill-in or a link at the caret of this field. */
  onActive: (insert: (name: string) => boolean) => void
  count: { label: string; over: boolean }
  /** The event's words, shown muted while the box is emptied and the service is sent them anyway. */
  placeholder: string
  /** The words the server was last asked about: what its drawing is of, for the lines it adds around them. */
  asked: string
}

/** What a card needs to let its title and description be edited in place. */
export type CardEditing = {
  title: CardField
  description: CardField
  /** The service has words of its own; the button says it can go back to the event's. */
  own: boolean
  useEventText: () => void
}

// The box sits inside the card without a frame of its own, in the card's text: a faint line under it
// says it can be typed in, and the frame comes back with the caret. A long word breaks rather than
// widen the dialog.
const IN_CARD =
  'h-auto min-h-[1.5em] whitespace-pre-wrap overflow-visible rounded-none border-transparent border-b-input bg-transparent px-0 py-0 text-[length:inherit] [overflow-wrap:anywhere] md:text-[length:inherit] focus-visible:border-ring focus-visible:ring-0'

/** A title or a description in a card: a box to type in, with the service's counter under it. */
export function CardText({
  field,
  label,
  multiline = false,
  links = true,
  className,
}: {
  field: CardField
  label: string
  multiline?: boolean
  links?: boolean
  className?: string
}) {
  return (
    <div className="flex min-w-0 flex-col gap-0.5">
      <FillInText
        label={label}
        multiline={multiline}
        links={links}
        value={field.value}
        onChange={field.onChange}
        onActive={field.onActive}
        placeholder={field.placeholder}
        className={cn(IN_CARD, multiline && 'min-h-[2.5em]', className)}
      />
      <span
        className={cn('self-end font-mono text-muted-foreground', field.count.over && 'text-destructive')}
        style={{ fontSize: 'var(--text-small)' }}
      >
        {field.count.label}
      </span>
    </div>
  )
}

/** The button that gives a service the event's words again. */
export function UseEventText({ editing }: { editing: CardEditing }) {
  if (!editing.own) return null

  return (
    <Button size="sm" variant="outline" className="self-start" onClick={editing.useEventText}>
      Use event text
    </Button>
  )
}

/** The lines the server drew above and below a text, in the card's muted colour. */
export function Around({ drawn, asked, where }: { drawn: string | null; asked: string; where: 'before' | 'after' }) {
  const lines = aroundOf(drawn, asked)?.[where]
  if (!lines) return null

  return <p className="whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">{lines}</p>
}
