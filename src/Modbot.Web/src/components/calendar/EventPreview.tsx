import { useEffect, useState, type ReactNode } from 'react'
import { CalendarDays, Lock, MapPin } from 'lucide-react'
import { BlueskyPost } from '@/components/marketing/PostPreview'
import { Outcome } from '@/components/settings/fields'
import { ApiError } from '@/lib/api'
import {
  calendarApi,
  CATEGORY_LABEL,
  PLATFORM_LABEL,
  type CalendarEventInput,
  type CalendarGooglePreview,
  type CalendarPreview,
} from '@/lib/calendar'
import type { BlueskyPostPreview } from '@/lib/postRules'
import { DESTINATION_LABEL, type CalendarDestination } from '@/lib/calendarPlaces'
import { discordPieces, discordTime } from '@/lib/discordText'
import { googleRepeatWords } from '@/lib/googleCalendar'
import { postAround } from '@/lib/calendarOwnText'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { cn } from '@/lib/utils'
import { Around, CardText, UseEventText, type CardEditing } from './CardText'

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

/** The same in the event's own zone, the way Google shows it; the viewer's when the zone is unknown here. */
function spanIn(startsAt: string, endsAt: string, timeZone: string): string {
  try {
    const zonedWhen = new Intl.DateTimeFormat(undefined, {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      timeZone,
    })
    const zonedTime = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit', timeZone })
    return `${zonedWhen.format(new Date(startsAt))} – ${zonedTime.format(new Date(endsAt))}`
  } catch {
    return span(startsAt, endsAt)
  }
}

/** What the server drew for the form: the cards, or why it could not. */
export type PreviewAnswer = { preview: CalendarPreview | null; error: string | null }

/**
 * The server's drawing of the event as `input` holds it (calendar design §14.2), asked again each
 * time `input` is a new object; null asks for nothing. The form holds this, so the last answer is
 * still on screen when a tab is left and come back to, and an old answer stays until the new one is in.
 */
export function usePreviewAnswer(eventId: string | null, input: CalendarEventInput | null): PreviewAnswer {
  const [answer, setAnswer] = useState<PreviewAnswer>({ preview: null, error: null })

  useEffect(() => {
    if (!input) return

    let cancelled = false

    calendarApi
      .preview(eventId, input)
      .then((preview) => {
        if (!cancelled) setAnswer({ preview, error: null })
      })
      .catch((e: unknown) => {
        if (!cancelled) setAnswer({ preview: null, error: e instanceof ApiError ? e.message : 'Could not draw the preview.' })
      })

    return () => {
      cancelled = true
    }
  }, [eventId, input])

  return answer
}

/**
 * A service's card in the form: the event drawn the way that place would show it, from the server's
 * answer. With one place there is one card, and its heading is the tab's name, so it is left to
 * screen readers.
 */
export function EventPreview({
  answer: { preview, error },
  places,
  vrchatPicture = null,
  discordPicture = null,
  editing = null,
}: {
  answer: PreviewAnswer
  /** The places to draw, in the chips' order. */
  places: CalendarDestination[]
  /**
   * Set, the card's title and description are boxes to type in, drawn from the form as it is now; the
   * server's answer still draws the rest, and the lines it adds around the words.
   */
  editing?: CardEditing | null
  /**
   * VRChat's picture as the form has it: a crop not uploaded yet, or the set picture when the page
   * can see it (calendar design §15.3). VRChat is sent only the file id, so the server cannot draw it.
   */
  vrchatPicture?: ReactNode
  /**
   * Discord's picture cropped and not uploaded yet (calendar design §15.4), drawn in place of the
   * cover and the post's picture. Once uploaded, the server's preview names it.
   */
  discordPicture?: ReactNode
}) {
  if (error) return <Outcome tone="problem">{error}</Outcome>
  if (!preview) return <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>Loading…</p>

  return (
    <div className={cn('flex min-w-0 flex-col gap-4', places.length === 1 && '[&_h3]:sr-only')} style={{ fontSize: 'var(--text-small)' }}>
      {places.includes('discordEvent') && preview.discordEvent && (
        <DiscordEventCard event={preview.discordEvent} picture={discordPicture} editing={editing} />
      )}
      {places.includes('channelPost') && preview.channelPost && (
        <ChannelPostCard post={preview.channelPost} picture={discordPicture} editing={editing} />
      )}
      {places.includes('vrchat') && <VRChatCard entry={preview.vrChat} picture={vrchatPicture} editing={editing} />}
      {places.includes('googleCalendar') && preview.google && <GoogleCard entry={preview.google} editing={editing} />}
      {places.includes('bluesky') && preview.bluesky && (
        <BlueskyPost post={preview.bluesky} body={editing && <BlueskyBody post={preview.bluesky} editing={editing} />} />
      )}
    </div>
  )
}

function Place({ place, editing, children }: { place: CalendarDestination; editing?: CardEditing | null; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1.5">
      <h3 className="font-label text-muted-foreground">{DESTINATION_LABEL[place]}</h3>
      {children}
      {editing && <UseEventText editing={editing} />}
    </section>
  )
}

/** Discord's own text in a card: times in the viewer's time, links as links. */
export function DiscordText({ text, now }: { text: string; now: Date }) {
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

function DiscordEventCard({
  event,
  picture,
  editing,
}: {
  event: NonNullable<CalendarPreview['discordEvent']>
  picture: ReactNode
  editing: CardEditing | null
}) {
  return (
    <Place place="discordEvent" editing={editing}>
      <div className="flex flex-col overflow-hidden rounded-sm border-(length:--hairline) bg-card">
        {picture ?? (event.coverUrl && (
          <img src={vrchatMedia(event.coverUrl)} alt="" className="aspect-[2.5/1] w-full object-cover" loading="lazy" />
        ))}
        <div className="flex flex-col gap-1.5 p-3">
          <div className="flex items-center gap-1.5 text-muted-foreground">
            <CalendarDays className="size-3.5 shrink-0" />
            {span(event.startsAt, event.endsAt)}
          </div>
          {editing ? (
            <div className="font-label" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
              <CardText field={editing.title} label="Title" links={false} />
            </div>
          ) : (
            <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
              {event.name}
            </div>
          )}
          <div className="flex items-center gap-1.5 [overflow-wrap:anywhere]">
            <MapPin className="size-3.5 shrink-0 text-muted-foreground" />
            {event.location}
          </div>
          {editing ? (
            <>
              <Around drawn={event.description} asked={editing.description.asked} where="before" />
              <CardText field={editing.description} label="Description" multiline />
              <Around drawn={event.description} asked={editing.description.asked} where="after" />
            </>
          ) : (
            event.description && <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">{event.description}</p>
          )}
        </div>
      </div>
    </Place>
  )
}

function ChannelPostCard({
  post,
  picture,
  editing,
}: {
  post: NonNullable<CalendarPreview['channelPost']>
  picture: ReactNode
  editing: CardEditing | null
}) {
  const now = new Date()
  const colour = `#${post.colour.toString(16).padStart(6, '0')}`

  return (
    <Place place="channelPost" editing={editing}>
      <div className="flex flex-col gap-2">
        {post.mentionRole && (
          <div>
            <RoleMention name={post.mentionRole} colour={post.mentionRoleColour ?? 0} />
          </div>
        )}
        <div
          className="flex flex-col gap-2 rounded-sm border-(length:--hairline) border-l-4 bg-card p-3"
          style={{ borderLeftColor: colour }}
        >
          {post.groupName && <div className="font-medium [overflow-wrap:anywhere]">{post.groupName}</div>}
          {editing ? (
            <div className={cn('font-label', post.titleLink && 'text-[#00a8fc]')}>
              <CardText field={editing.title} label="Title" links={false} />
            </div>
          ) : (
            <div className="font-label [overflow-wrap:anywhere]">
              {post.titleLink ? (
                <a href={post.titleLink} target="_blank" rel="noreferrer" className="text-[#00a8fc] hover:underline">
                  {post.title}
                </a>
              ) : (
                post.title
              )}
            </div>
          )}
          {editing ? (
            <CardText field={editing.description} label="Description" multiline />
          ) : (
            post.description && <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">{post.description}</p>
          )}
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
          {picture ??
            (post.pictureUrl && (
              <img src={vrchatMedia(post.pictureUrl)} alt="" className="max-h-64 w-full rounded-sm object-cover" loading="lazy" />
            ))}
          {post.footer && <div className="text-muted-foreground [overflow-wrap:anywhere]">{post.footer}</div>}
        </div>
        {post.buttons.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {post.buttons.map((b) => (
              <a
                key={b.url}
                href={b.url}
                target="_blank"
                rel="noreferrer"
                className="rounded-sm border-(length:--hairline) bg-muted px-3 py-1 font-medium [overflow-wrap:anywhere]"
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

/** A role mention as Discord draws it: the role's name in its colour, on a faint wash of it. */
export function RoleMention({ name, colour }: { name: string; colour: number }) {
  if (!colour) {
    return <span className="rounded-sm bg-[#5865f2]/20 px-0.5 font-medium text-[#5865f2]">@{name}</span>
  }

  const hex = `#${colour.toString(16).padStart(6, '0')}`
  return (
    <span className="rounded-sm px-0.5 font-medium" style={{ color: hex, backgroundColor: `${hex}26` }}>
      @{name}
    </span>
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

function VRChatCard({ entry, picture, editing }: { entry: CalendarPreview['vrChat']; picture: ReactNode; editing: CardEditing | null }) {
  return (
    <Place place="vrchat" editing={editing}>
      <div className="flex flex-col gap-1.5 rounded-sm border-(length:--hairline) bg-card p-3">
        {picture}
        {editing ? (
          <div className="font-label" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
            <CardText field={editing.title} label="Title" links={false} />
          </div>
        ) : (
          <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
            {entry.title}
          </div>
        )}
        <Row label="When">{span(entry.startsAt, entry.endsAt)}</Row>
        <Row label="Repeats">
          {entry.repeat &&
            [
              entry.repeat.frequency,
              entry.repeat.every > 1 ? `every ${entry.repeat.every}` : null,
              join(entry.repeat.days),
              entry.repeat.until,
              entry.repeat.times !== null ? `${entry.repeat.times} times` : null,
              entry.repeat.timeZone,
            ]
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
        <Row label="Featured">{entry.featured ? 'Yes' : 'No'}</Row>
        {editing ? (
          <div className="pt-1">
            <CardText field={editing.description} label="Description" multiline />
          </div>
        ) : (
          entry.description && <p className="pt-1 whitespace-pre-wrap [overflow-wrap:anywhere]">{entry.description}</p>
        )}
      </div>
    </Place>
  )
}

/** The Google event as the Google loop sends it, times in the event's own zone as Google shows them. */
function GoogleCard({ entry, editing }: { entry: CalendarGooglePreview; editing: CardEditing | null }) {
  return (
    <Place place="googleCalendar" editing={editing}>
      <div className="flex flex-col gap-1.5 rounded-sm border-(length:--hairline) bg-card p-3">
        <div className="flex items-center gap-1.5 font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
          {entry.private && <Lock className="size-3.5 shrink-0 text-muted-foreground" aria-label="Private" />}
          {editing ? (
            <div className="min-w-0 flex-1">
              <CardText field={editing.title} label="Title" links={false} />
            </div>
          ) : (
            entry.title
          )}
        </div>
        <Row label="When">
          {spanIn(entry.startsAt, entry.endsAt, entry.timeZone)} · {entry.timeZone}
        </Row>
        <Row label="Repeats">{entry.repeat && googleRepeatWords(entry.repeat)}</Row>
        <Row label="Location">{entry.location}</Row>
        <Row label="Calendar">{entry.calendarName}</Row>
        {editing ? (
          <div className="flex flex-col gap-1.5 pt-1">
            <Around drawn={entry.description} asked={editing.description.asked} where="before" />
            <CardText field={editing.description} label="Description" multiline />
            <Around drawn={entry.description} asked={editing.description.asked} where="after" />
          </div>
        ) : (
          entry.description && <p className="pt-1 whitespace-pre-wrap [overflow-wrap:anywhere]">{entry.description}</p>
        )}
      </div>
    </Place>
  )
}

/**
 * A Bluesky post's text with the title and the description as boxes to type in, and the time, the
 * world and the link the server put around them as it drew them.
 */
function BlueskyBody({ post, editing }: { post: BlueskyPostPreview; editing: CardEditing }) {
  const lines = postAround(post.text, editing.title.asked, editing.description.asked)

  return (
    <div className="flex flex-col gap-1.5">
      <CardText field={editing.title} label="Title" links={false} />
      {lines.before && <p className="whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">{lines.before}</p>}
      <CardText field={editing.description} label="Description" multiline />
      {lines.after && <p className="whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">{lines.after}</p>}
    </div>
  )
}
