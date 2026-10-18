import { Check } from 'lucide-react'
import { SwitchBank } from '@/components/ui/switch-bank'
import { SET_UP_LINK } from '@/lib/calendarPlaces'
import { GOOGLE_COLOURS, SHOW_AS_LABEL, type GoogleShowAs } from '@/lib/googleColours'
import { useCanManageSettings } from '@/lib/manageSettings'
import { cn } from '@/lib/utils'

const SHOW_AS_OPTIONS = (Object.keys(SHOW_AS_LABEL) as GoogleShowAs[]).map((value) => ({ value, label: SHOW_AS_LABEL[value] }))

/** A small muted label over a control, for rows that are not a single input. */
function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {children}
    </div>
  )
}

/**
 * The Google Calendar tab's settings, under its preview: the calendar the event goes to (linking
 * to where it is set up), the event's colour as Google's own palette, and busy or free.
 */
export function GoogleChoices({
  calendarName,
  colour,
  onColour,
  showAs,
  onShowAs,
}: {
  /** The calendar's name as Check found it; null before one is saved. */
  calendarName: string | null
  /** Google's colour id; blank for the calendar's own. */
  colour: string
  onColour: (id: string) => void
  showAs: GoogleShowAs
  onShowAs: (next: GoogleShowAs) => void
}) {
  const mayFollow = useCanManageSettings()

  return (
    <>
      {calendarName && (
        <Row label="Calendar">
          {mayFollow ? (
            <a
              href={SET_UP_LINK.googleCalendar}
              target="_blank"
              rel="noreferrer"
              className="self-start underline underline-offset-2"
              style={{ fontSize: 'var(--text-base)' }}
            >
              {calendarName}
            </a>
          ) : (
            <span style={{ fontSize: 'var(--text-base)' }}>{calendarName}</span>
          )}
        </Row>
      )}
      <Row label="Colour">
        <div role="radiogroup" aria-label="Colour" className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            role="radio"
            aria-checked={colour === ''}
            onClick={() => onColour('')}
            className={cn(
              'inline-flex items-center rounded-sm border border-(length:--hairline) border-input px-2.5 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
              colour === '' ? 'bg-accent text-accent-foreground' : 'bg-card text-muted-foreground hover:bg-muted hover:text-foreground',
            )}
            style={{ fontSize: 'var(--text-small)', height: 'var(--control-h)' }}
          >
            Calendar default
          </button>
          {GOOGLE_COLOURS.map((c) => (
            <button
              key={c.id}
              type="button"
              role="radio"
              aria-checked={colour === c.id}
              aria-label={c.name}
              title={c.name}
              onClick={() => onColour(c.id)}
              className={cn(
                'flex size-7 items-center justify-center rounded-full border border-(length:--hairline) border-black/20 transition-shadow focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
                colour === c.id && 'ring-2 ring-ring ring-offset-2 ring-offset-background',
              )}
              style={{ background: c.hex }}
            >
              {colour === c.id && <Check className="size-4 text-black/70" aria-hidden />}
            </button>
          ))}
        </div>
      </Row>
      <Row label="Show as">
        <SwitchBank value={showAs} onChange={onShowAs} options={SHOW_AS_OPTIONS} label="Show as" className="self-start" />
      </Row>
    </>
  )
}
