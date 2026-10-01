import { useEffect, useState, type ReactNode } from 'react'
import { CalendarDays, MapPin } from 'lucide-react'
import { Outcome } from '@/components/settings/fields'
import { ApiError } from '@/lib/api'
import {
  calendarApi,
  CATEGORY_LABEL,
  PLATFORM_LABEL,
  type CalendarEventInput,
  type CalendarPreview,
} from '@/lib/calendar'
import { DESTINATION_LABEL, type CalendarDestination } from '@/lib/calendarPlaces'
import { discordPieces, discordTime } from '@/lib/discordText'
import { vrchatMedia } from '@/lib/vrchatMedia'

const when = new Intl.DateTimeFormat(undefined, {
  weekday: 'short',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
})
const time = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' })

/** "Fri, Oct 2, 8:00 PM – 10:00 PM" in the viewer's own time. */
function span(startsAt: string, endsAt: string): string {
  return `${when.format(new Date(startsAt))} – ${time.format(new Date(endsAt))}`
}

/**
 * The form's Preview: the event drawn the way each place it goes would show it, by the server from
 * the code that sends it (calendar design §14.2). Read once when it opens; the form's fields are not
 * on screen while it is, so nothing can change under it.
 */
export function EventPreview({
  eventId,
  input,
  places,
}: {
  eventId: string | null
  input: CalendarEventInput
  /** The chips that are on, the feed among them, in the chips' order. */
  places: CalendarDestination[]
}) {
  const [preview, setPreview] = useState<CalendarPreview | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    calendarApi
      .preview(eventId, input)
      .then((p) => {
        if (!cancelled) setPreview(p)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not draw the preview.')
      })

    return () => {
      cancelled = true
    }
  }, [eventId, input])

  if (error) return <Outcome tone="problem">{error}</Outcome>
  if (!preview) return <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>Loading…</p>

  return (
    <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
      {places.includes('discordEvent') && preview.discordEvent && <DiscordEventCard event={preview.discordEvent} />}
      {places.includes('channelPost') && preview.channelPost && <ChannelPostCard post={preview.channelPost} />}
      {places.includes('vrchat') && <VRChatCard entry={preview.vrChat} />}
      {places.includes('feed') && <FeedCard entry={preview.feed} />}
    </div>
  )
}

function Place({ place, children }: { place: CalendarDestination; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1.5">
      <h3 className="font-label text-muted-foreground">{DESTINATION_LABEL[place]}</h3>
      {children}
    </section>
  )
}

/** Discord's own text in a card: times in the viewer's time, links as links. */
function DiscordText({ text, now }: { text: string; now: Date }) {
  return (
    <>
      {discordPieces(text).map((piece, i) =>
        piece.kind === 'text' ? (
          <span key={i}>{piece.text}</span>
        ) : piece.kind === 'time' ? (
          <span key={i} className="rounded-sm bg-muted px-0.5">
            {discordTime(piece.at, piece.style, now)}
          </span>
        ) : (
          <a key={i} href={piece.url} target="_blank" rel="noreferrer" className="text-[#00a8fc] hover:underline">
            {piece.label}
          </a>
        ),
      )}
    </>
  )
}

function DiscordEventCard({ event }: { event: NonNullable<CalendarPreview['discordEvent']> }) {
  return (
    <Place place="discordEvent">
      <div className="flex flex-col overflow-hidden rounded-sm border-(length:--hairline) bg-card">
        {event.coverUrl && (
          <img src={vrchatMedia(event.coverUrl)} alt="" className="aspect-[2.5/1] w-full object-cover" loading="lazy" />
        )}
        <div className="flex flex-col gap-1.5 p-3">
          <div className="flex items-center gap-1.5 text-muted-foreground">
            <CalendarDays className="size-3.5 shrink-0" />
            {span(event.startsAt, event.endsAt)}
          </div>
          <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
            {event.name}
          </div>
          <div className="flex items-center gap-1.5 [overflow-wrap:anywhere]">
            <MapPin className="size-3.5 shrink-0 text-muted-foreground" />
            {event.location}
          </div>
          {event.description && <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">{event.description}</p>}
        </div>
      </div>
    </Place>
  )
}

function ChannelPostCard({ post }: { post: NonNullable<CalendarPreview['channelPost']> }) {
  const now = new Date()
  const colour = `#${post.colour.toString(16).padStart(6, '0')}`

  return (
    <Place place="channelPost">
      <div className="flex flex-col gap-2">
        <div
          className="flex flex-col gap-2 rounded-sm border-(length:--hairline) border-l-4 bg-card p-3"
          style={{ borderLeftColor: colour }}
        >
          {post.groupName && <div className="font-medium">{post.groupName}</div>}
          <div className="font-label [overflow-wrap:anywhere]">
            {post.titleLink ? (
              <a href={post.titleLink} target="_blank" rel="noreferrer" className="text-[#00a8fc] hover:underline">
                {post.title}
              </a>
            ) : (
              post.title
            )}
          </div>
          {post.description && <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">{post.description}</p>}
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
            {post.fields.map((f) => (
              <div key={f.name} className={f.inline ? undefined : 'sm:col-span-3'}>
                <div className="font-medium">{f.name}</div>
                <div className="[overflow-wrap:anywhere]">
                  <DiscordText text={f.value} now={now} />
                </div>
              </div>
            ))}
          </div>
          {post.pictureUrl && (
            <img src={vrchatMedia(post.pictureUrl)} alt="" className="max-h-64 w-full rounded-sm object-cover" loading="lazy" />
          )}
          {post.footer && <div className="text-muted-foreground">{post.footer}</div>}
        </div>
        {post.buttons.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {post.buttons.map((b) => (
              <a
                key={b.url}
                href={b.url}
                target="_blank"
                rel="noreferrer"
                className="rounded-sm border-(length:--hairline) bg-muted px-3 py-1 font-medium"
              >
                {b.label}
              </a>
            ))}
          </div>
        )}
      </div>
    </Place>
  )
}

/** One labelled line of an entry, left out when there is nothing to show. */
function Row({ label, children }: { label: string; children: ReactNode }) {
  if (children === null || children === undefined || children === '') return null

  return (
    <div className="grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-3">
      <span className="text-muted-foreground">{label}</span>
      <span className="min-w-0 [overflow-wrap:anywhere]">{children}</span>
    </div>
  )
}

const join = (values: string[]) => (values.length > 0 ? values.join(', ') : null)

function VRChatCard({ entry }: { entry: CalendarPreview['vrChat'] }) {
  return (
    <Place place="vrchat">
      <div className="flex flex-col gap-1.5 rounded-sm border-(length:--hairline) bg-card p-3">
        <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
          {entry.title}
        </div>
        <Row label="When">{span(entry.startsAt, entry.endsAt)}</Row>
        <Row label="Repeats">
          {entry.repeat &&
            [entry.repeat.frequency, join(entry.repeat.days), entry.repeat.until, entry.repeat.timeZone]
              .filter(Boolean)
              .join(' · ')}
        </Row>
        <Row label="Category">{entry.category && (CATEGORY_LABEL[entry.category] ?? entry.category)}</Row>
        <Row label="Visible to">
          {entry.visibility && ({ group: 'Group', public: 'Everyone' }[entry.visibility] ?? entry.visibility)}
        </Row>
        <Row label="Languages">{join(entry.languages)}</Row>
        <Row label="Platforms">{join(entry.platforms.map((p) => PLATFORM_LABEL[p] ?? p))}</Row>
        <Row label="Tags">{join(entry.tags)}</Row>
        <Row label="VRChat image id">{entry.imageId && <span className="font-mono">{entry.imageId}</span>}</Row>
        <Row label="Notify group members">{entry.notify ? 'Yes' : 'No'}</Row>
        {entry.description && <p className="pt-1 whitespace-pre-wrap [overflow-wrap:anywhere]">{entry.description}</p>}
      </div>
    </Place>
  )
}

function FeedCard({ entry }: { entry: CalendarPreview['feed'] }) {
  return (
    <Place place="feed">
      <div className="flex flex-col gap-1.5 rounded-sm border-(length:--hairline) bg-card p-3">
        <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
          {entry.title}
        </div>
        <Row label="When">{span(entry.startsAt, entry.endsAt)}</Row>
        <Row label="Repeats">{entry.repeat && <span className="font-mono">{entry.repeat}</span>}</Row>
        <Row label="Location">{entry.location}</Row>
        <Row label="Calendar">{entry.calendarName}</Row>
        {entry.notes && <p className="pt-1 whitespace-pre-wrap [overflow-wrap:anywhere]">{entry.notes}</p>}
      </div>
    </Place>
  )
}
