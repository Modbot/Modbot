import type { ReactNode } from 'react'
import { AlignLeft, Lock, MapPin, Repeat } from 'lucide-react'
// Design mock-up: the event the way Google Calendar's own event popup shows it (its light theme).
// The title and the description are boxes to type in; the rest is what the server says the Google
// calendar is sent.

const CARD = '#ffffff'
const TEXT = '#3c4043'
const MUTED = '#70757a'
const LINE = '#dadce0'

type Props = {
  titleBox: ReactNode
  about: ReactNode
  /** The lines the server puts before and after the description's words (the world's name and page). */
  before?: string
  after?: string
  aboutCount?: { label: string; over: boolean }
  /** "Friday, October 17 ⋅ 8:00 – 9:00 PM" in the event's zone; null before the server has drawn it. */
  when: string | null
  /** "Weekly on Friday"; null for an event that does not repeat. */
  repeat: string | null
  location: string | null
  /** The calendar's name as Check found it; null before one is saved. */
  calendarName: string | null
  /** Only the people the calendar is shared with see the event. */
  isPrivate: boolean
  /** The colour square, as `#rrggbb`. */
  colour: string
}

function Row({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <div className="flex min-w-0 items-start gap-4">
      <span className="flex h-5 w-5 shrink-0 items-center justify-center" style={{ color: MUTED }}>
        {icon}
      </span>
      <div className="min-w-0 flex-1 [overflow-wrap:anywhere]">{children}</div>
    </div>
  )
}

/** The event the way Google Calendar's event popup draws it, with its title and description to type in. */
export function GooglePreviews({ titleBox, about, before, after, aboutCount, when, repeat, location, calendarName, isPrivate, colour }: Props) {
  return (
    <div className="max-w-full overflow-x-auto">
      <div
        className="flex w-full max-w-[448px] flex-col gap-3 rounded-lg p-5 shadow-md"
        style={{ background: CARD, color: TEXT, border: `1px solid ${LINE}`, fontSize: 14, lineHeight: '20px' }}
      >
        <div className="flex min-w-0 items-start gap-4">
          <span className="mt-1.5 flex h-5 w-5 shrink-0 items-center justify-center">
            <span data-testid="google-colour" className="size-3.5 rounded-[3px]" style={{ background: colour }} />
          </span>
          <div className="min-w-0 flex-1" style={{ fontSize: 22, lineHeight: '28px' }}>
            {titleBox}
            <div className="mt-0.5" style={{ fontSize: 14, lineHeight: '20px' }}>
              {when ?? ' '}
            </div>
            {repeat && (
              <div className="flex items-center gap-1.5" style={{ color: MUTED, fontSize: 14, lineHeight: '20px' }}>
                <Repeat className="size-3.5 shrink-0" aria-hidden />
                {repeat}
              </div>
            )}
          </div>
        </div>
        {location && (
          <Row icon={<MapPin className="size-5" aria-hidden />}>
            <div>{location}</div>
          </Row>
        )}
        <Row icon={<AlignLeft className="size-5" aria-hidden />}>
          <div className="flex flex-col gap-1.5">
            {before && <p className="whitespace-pre-wrap" style={{ color: MUTED }}>{before}</p>}
            {about}
            {after && <p className="whitespace-pre-wrap" style={{ color: MUTED }}>{after}</p>}
            {aboutCount && (
              <span className="self-end font-mono" style={{ fontSize: 12, color: aboutCount.over ? '#d93025' : MUTED }}>
                {aboutCount.label}
              </span>
            )}
          </div>
        </Row>
        {calendarName && (
          <Row icon={<span className="size-3.5 rounded-[3px]" style={{ background: colour }} />}>
            <span>{calendarName}</span>
          </Row>
        )}
        {isPrivate && (
          <Row icon={<Lock className="size-4" aria-hidden />}>
            <span>Private</span>
          </Row>
        )}
      </div>
    </div>
  )
}
