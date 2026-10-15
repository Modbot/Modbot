import { useRef, useState, type ReactNode } from 'react'
import { CalendarDays, Ellipsis, ExternalLink, Link2, MapPin, Bell, Users, X } from 'lucide-react'
import type { CalendarPreview } from '@/lib/calendar'
import { DiscordText, RoleMention } from './EventPreview'
// Design mock-up: the event the way Discord shows it. Sizes measured in Discord's web app
// (.agent/research/2026-10-15-discord-event-views.md); colours are Discord's dark theme with the tint taken out.

const APP_BG = '#1f1f23'
const MODAL = '#28282c'
const CARD = '#2a2a2f'
const LINE = 'rgba(109,109,115,0.2)'
const TEXT = '#efeff0'
const TITLE = '#f8f8f9'
const MUTED = '#9b9ba3'
const BUTTON = 'rgba(151,151,159,0.12)'
const BLURPLE = '#5865f2'
/** The scheduled post's colour when none is picked: the server's `CardColour.Violet`. */
const MODBOT_VIOLET = 0x5b4bd6
const LINK = '#00a8fc'

type Post = NonNullable<CalendarPreview['channelPost']>

type Props = {
  /** Which tab: the Discord event's views, or the channel post's. */
  place: 'discordEvent' | 'channelPost'
  title: string
  description: string
  startsAt: string
  endsAt: string
  /** Where the event is, as Discord shows it under the title. */
  location: string | null
  /** A crop not uploaded yet; drawn in place of the picture link. */
  picture: ReactNode
  pictureUrl: string | null
  /** A picture dropped or picked on a preview's picture. */
  onPicture?: (file: Blob) => void
  titleBox?: ReactNode
  about?: ReactNode
  aboutCount?: { label: string; over: boolean }
  groupName?: string | null
  post?: Post | null
  /** The post's colour, 0xRRGGBB, null for Modbot's own; with a setter, the bar picks it. */
  colour?: number | null
  onColour?: (colour: number | null) => void
}

const EVENT_VIEWS = ['Events list', 'Event page', 'Link in chat'] as const
const POST_VIEWS = ['In the channel'] as const
type View = (typeof EVENT_VIEWS)[number] | (typeof POST_VIEWS)[number]

// Kept while the page is open, so switching form tabs keeps the view.
const lastViews: Record<Props['place'], View[]> = { discordEvent: ['Event page'], channelPost: ['In the channel'] }
let lastScale = 1

const clock = (iso: string) => new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })

/** Discord's own day words: "Today at 5:00 PM", "Tomorrow at 6:00 PM", "Sat Oct 17th · 6:00 PM". */
function discordWhen(iso: string) {
  const at = new Date(iso)
  const now = new Date()
  const days = Math.round((new Date(at).setHours(0, 0, 0, 0) - new Date(now).setHours(0, 0, 0, 0)) / 86_400_000)
  if (days === 0) return `Today at ${clock(iso)}`
  if (days === 1) return `Tomorrow at ${clock(iso)}`
  const d = at.getDate()
  const th = d % 10 === 1 && d !== 11 ? 'st' : d % 10 === 2 && d !== 12 ? 'nd' : d % 10 === 3 && d !== 13 ? 'rd' : 'th'
  const day = at.toLocaleDateString(undefined, { weekday: 'short', month: 'short' })
  return `${day} ${d}${th} · ${clock(iso)}`
}

function Shown({ name, view, children }: { name: View; view: View[]; children: ReactNode }) {
  if (!view.includes(name)) return null
  return <div className="max-w-full overflow-x-auto">{children}</div>
}

/** The picture, or an empty spot to drop or pick one; clicking it picks a file. */
function Cover({ p, className, rounded }: { p: Props; className: string; rounded: string }) {
  const file = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const has = p.picture || p.pictureUrl
  if (!has && !p.onPicture) return null
  return (
    <div
      className={`group relative overflow-hidden bg-cover bg-center ${rounded} ${className} ${p.onPicture ? 'cursor-pointer' : ''}`}
      style={!p.picture && p.pictureUrl ? { backgroundImage: `url(${p.pictureUrl})` } : { background: has ? undefined : 'rgba(151,151,159,0.08)' }}
      onClick={() => file.current?.click()}
      onDragOver={(e) => {
        e.preventDefault()
        setOver(true)
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => {
        e.preventDefault()
        setOver(false)
        const f = e.dataTransfer.files[0]
        if (f) p.onPicture?.(f)
      }}
    >
      {p.picture && <div className="absolute inset-0 [&>*]:size-full [&_img]:size-full [&_img]:object-cover">{p.picture}</div>}
      {p.onPicture && (
        <>
          <span
            className={`absolute inset-0 flex items-center justify-center text-[14px] font-semibold transition-opacity ${has ? 'bg-black/50 opacity-0 group-hover:opacity-100' : ''} ${over ? 'opacity-100' : ''}`}
            style={{ color: TEXT }}
          >
            {has ? 'Change picture' : 'Add a picture'}
          </span>
          <input
            ref={file}
            type="file"
            accept="image/*"
            className="hidden"
            onChange={(e) => {
              const f = e.target.files?.[0]
              if (f) p.onPicture?.(f)
              e.target.value = ''
            }}
          />
        </>
      )}
    </div>
  )
}

function Btn({ children, big, icon }: { children?: ReactNode; big?: boolean; icon?: boolean }) {
  return (
    <span
      className={`inline-flex shrink-0 items-center justify-center gap-1.5 rounded-lg border text-[14px] font-medium ${big ? 'h-10 px-4' : 'h-8 px-3'} ${icon ? (big ? 'w-10 px-0' : 'w-8 px-0') : ''}`}
      style={{ background: BUTTON, borderColor: 'rgba(109,109,115,0.08)', color: '#fff' }}
    >
      {children}
    </span>
  )
}

function ServerIcon({ size }: { size: number }) {
  return <span className="inline-block shrink-0 rounded-full" style={{ width: size, height: size, background: BLURPLE }} />
}

function Count({ count }: { count: { label: string; over: boolean } | undefined }) {
  if (!count) return null
  return <span className={`font-mono text-[12px] ${count.over ? 'text-red-400' : 'opacity-60'}`}>{count.label}</span>
}

export function DiscordPreviews(p: Props) {
  const views: readonly View[] = p.place === 'discordEvent' ? EVENT_VIEWS : POST_VIEWS
  const [view, setViewState] = useState<View[]>(lastViews[p.place])
  const setView = (v: View) => {
    const next = view.includes(v) ? view.filter((x) => x !== v) : views.filter((x) => x === v || view.includes(x))
    lastViews[p.place] = next
    setViewState(next)
  }
  const [scale, setScaleState] = useState(lastScale)
  const setScale = (s: number) => {
    lastScale = s
    setScaleState(s)
  }
  const title = p.title || 'Untitled event'
  const now = new Date()

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap gap-1.5">
        {views.map((v) => (
          <button
            key={v}
            type="button"
            onClick={() => setView(v)}
            className={`rounded-sm border border-(length:--hairline) px-2 py-1 transition-colors ${view.includes(v) ? 'border-primary bg-primary text-primary-foreground' : 'border-input text-muted-foreground hover:text-foreground'}`}
            style={{ fontSize: 'var(--text-small)' }}
          >
            {v}
          </button>
        ))}
      </div>
      <label className="flex items-center gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Size
        <input
          type="range"
          min={0.5}
          max={2}
          step={0.05}
          value={scale}
          onChange={(e) => setScale(Number(e.target.value))}
          onDoubleClick={() => setScale(1)}
          className="w-48 accent-primary"
        />
        <span className="w-10 font-mono">{Math.round(scale * 100)}%</span>
      </label>
      <div className="overflow-auto rounded-sm border border-(length:--hairline) border-input p-2.5" style={{ background: APP_BG }}>
        <div className="flex flex-wrap items-start gap-4" style={{ zoom: scale, color: TEXT, fontFamily: '"gg sans", "Noto Sans", "Helvetica Neue", Helvetica, Arial, sans-serif' }}>
          {/* The server's Events list: 552 wide, cover cut to 120 high, title on one line, description 4 lines. */}
          <Shown view={view} name="Events list">
            <div className="w-[554px] overflow-hidden rounded-lg border" style={{ background: CARD, borderColor: LINE }}>
              <Cover p={p} className="h-[120px] w-full" rounded="rounded-t-lg" />
              <div className="flex flex-col p-4">
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-2 text-[14px] leading-[18px] font-semibold">
                    <CalendarDays className="size-5" style={{ color: MUTED }} />
                    {discordWhen(p.startsAt)}
                  </span>
                  <span className="flex items-center gap-1 text-[12px]" style={{ color: MUTED }}>
                    <Users className="size-3.5" /> 0
                  </span>
                </div>
                <div className="mt-2 truncate text-[20px] leading-6 font-medium" style={{ color: TITLE }}>{title}</div>
                {p.description && (
                  <div className="mt-2 line-clamp-4 text-[14px] leading-[18px] [overflow-wrap:anywhere] whitespace-break-spaces">
                    <DiscordText text={p.description} now={now} />
                  </div>
                )}
                <div className="mt-4 flex items-center justify-between gap-2">
                  <span className="flex min-w-0 items-center gap-1.5 text-[14px] leading-[18px]">
                    <MapPin className="size-4 shrink-0" style={{ color: MUTED }} />
                    <span className="truncate">{p.location ?? 'Somewhere'}</span>
                  </span>
                  <span className="flex gap-2">
                    <Btn icon><Ellipsis className="size-4" /></Btn>
                    <Btn><Link2 className="size-4" /> Copy Link</Btn>
                    <Btn><Bell className="size-4" /> Interested</Btn>
                  </span>
                </div>
              </div>
            </div>
          </Shown>

          {/* One event opened: 680 wide, cover 630 x 240, every word of the description. */}
          <Shown view={view} name="Event page">
            <div className="w-[680px] rounded-xl border p-6" style={{ background: MODAL, borderColor: 'rgba(109,109,115,0.2)' }}>
              <div className="flex items-start justify-between gap-4">
                <div className="truncate text-[20px] leading-6 font-semibold" style={{ color: TITLE }}>{title}</div>
                <X className="size-6 shrink-0" style={{ color: MUTED }} />
              </div>
              <Cover p={p} className="mt-4 h-[240px] w-full" rounded="rounded-lg" />
              <div className="mt-6 flex gap-6 border-b text-[14px] font-semibold" style={{ borderColor: LINE }}>
                <span className="border-b-2 pb-3" style={{ borderColor: TITLE, color: TITLE }}>Event Info</span>
                <span className="pb-3" style={{ color: MUTED }}>0 Interested</span>
              </div>
              <div className="mt-5 flex items-center gap-2 text-[14px] leading-[18px] font-semibold">
                <CalendarDays className="size-5" style={{ color: MUTED }} />
                {discordWhen(p.startsAt)} — {clock(p.endsAt)}
              </div>
              <div className="mt-3 flex items-center justify-between gap-2">
                <div className="min-w-0 flex-1 text-[20px] leading-6 font-semibold" style={{ color: TITLE }}>{p.titleBox ?? title}</div>
              </div>
              <div className="mt-3 flex flex-col gap-1.5 text-[14px] leading-[18px]">
                <span className="flex items-center gap-2"><ServerIcon size={20} /> {p.groupName ?? 'Your server'}</span>
                <span className="flex items-center gap-2"><MapPin className="size-5" style={{ color: MUTED }} /> {p.location ?? 'Somewhere'}</span>
                <span className="flex items-center gap-2"><Users className="size-5" style={{ color: MUTED }} /> 0 people are interested</span>
                <span className="flex items-center gap-2"><ServerIcon size={20} /> Created by Modbot</span>
              </div>
              <div className="mt-4 flex items-start justify-between gap-2 text-[14px] leading-[18px] whitespace-pre-wrap [overflow-wrap:anywhere]">
                <div className="min-w-0 flex-1">{p.about ?? <DiscordText text={p.description} now={now} />}</div>
                <Count count={p.aboutCount} />
              </div>
              <div className="mt-6 flex justify-end gap-2">
                <Btn big icon><Ellipsis className="size-5" /></Btn>
                <Btn big><Link2 className="size-5" /> Copy Link</Btn>
                <Btn big><Bell className="size-5" /> Interested</Btn>
              </div>
            </div>
          </Shown>

          {/* The event's link pasted in a channel. Not measured: drawn from Discord's invite card. */}
          <Shown view={view} name="Link in chat">
            <Message now={now}>
              <div className="text-[16px] leading-[22px]" style={{ color: LINK }}>https://discord.gg/…?event=…</div>
              <div className="mt-1 w-[432px] overflow-hidden rounded-lg" style={{ background: CARD }}>
                <Cover p={p} className="h-[173px] w-full" rounded="" />
                <div className="p-4">
                  <div className="text-[12px] font-bold tracking-wide uppercase" style={{ color: MUTED }}>{discordWhen(p.startsAt)}</div>
                  <div className="mt-1 truncate text-[16px] leading-5 font-semibold" style={{ color: TITLE }}>{title}</div>
                  <div className="mt-2 flex items-center gap-2 text-[14px]" style={{ color: MUTED }}>
                    <ServerIcon size={16} /> {p.groupName ?? 'Your server'}
                  </div>
                  <div className="mt-1 flex items-center gap-2 text-[14px]" style={{ color: MUTED }}>
                    <MapPin className="size-4" /> {p.location ?? 'Somewhere'}
                  </div>
                  <div className="mt-3 flex gap-2">
                    <span className="flex h-8 flex-1 items-center justify-center rounded-lg text-[14px] font-medium text-white" style={{ background: BLURPLE }}>Join Server</span>
                    <Btn><Bell className="size-4" /> Interested</Btn>
                  </div>
                </div>
              </div>
            </Message>
          </Shown>

          <Shown view={view} name="In the channel">
            <Message now={now} mention={p.post}>
              <PostEmbed p={p} now={now} width="max-w-[516px]" />
              <PostButtons post={p.post} />
            </Message>
          </Shown>

        </div>
      </div>
    </div>
  )
}

/** A message from Modbot: avatar, name, APP tag and time, then the content. */
function Message({ now, mention, narrow, children }: { now: Date; mention?: Post | null; narrow?: boolean; children: ReactNode }) {
  return (
    <div className={`flex gap-4 py-1 ${narrow ? 'w-full' : 'w-[640px]'}`}>
      <span className="mt-0.5 size-10 shrink-0 rounded-full" style={{ background: BLURPLE }} />
      <div className="min-w-0 flex-1">
        <div className="flex items-baseline gap-1.5">
          <span className="text-[16px] leading-[22px] font-medium" style={{ color: TITLE }}>Modbot</span>
          <span className="rounded px-1 text-[10px] leading-[15px] font-semibold text-white" style={{ background: BLURPLE }}>APP</span>
          <span className="text-[12px]" style={{ color: MUTED }}>Today at {clock(now.toISOString())}</span>
        </div>
        {mention?.mentionRole && (
          <div className="text-[16px] leading-[22px]">
            <RoleMention name={mention.mentionRole} colour={mention.mentionRoleColour ?? 0} />
          </div>
        )}
        <div className="mt-0.5 flex flex-col gap-1">{children}</div>
      </div>
    </div>
  )
}

/** The post's card: 4px colour bar, 8/16/16/12 padding, at most 516 wide (measured). */
function PostEmbed({ p, now, width, narrow }: { p: Props; now: Date; width: string; narrow?: boolean }) {
  const post = p.post
  // The bar is the server's colour for the post as it would read now (an open date is green whatever
  // is picked); the picker holds the colour picked for while it is scheduled.
  const hex = (n: number) => `#${n.toString(16).padStart(6, '0')}`
  const colour = hex(post?.colour ?? p.colour ?? MODBOT_VIOLET)
  const picked = hex(p.colour ?? MODBOT_VIOLET)
  return (
    <div className={`relative grid rounded border-l-4 pt-2 pr-4 pb-4 pl-3 ${width}`} style={{ background: BUTTON, borderLeftColor: colour }}>
      {p.onColour && (
        <label className="absolute inset-y-0 -left-2 w-4 cursor-pointer" title="Colour">
          <input
            type="color"
            aria-label="Colour"
            value={picked}
            onChange={(e) => p.onColour!(parseInt(e.target.value.slice(1), 16))}
            className="absolute inset-0 size-full cursor-pointer opacity-0"
          />
        </label>
      )}
      {post?.groupName && <div className="mt-2 text-[14px] leading-[22px] font-semibold" style={{ color: TITLE }}>{post.groupName}</div>}
      <div className="mt-2 flex items-center gap-2 text-[16px] leading-[22px] font-semibold" style={{ color: post?.titleLink ? LINK : TITLE }}>
        <div className="min-w-0 flex-1">{p.titleBox ?? p.title}</div>
      </div>
      <div className="mt-2 flex items-start gap-2 text-[14px] leading-[18px] whitespace-pre-wrap [overflow-wrap:anywhere]">
        <div className="min-w-0 flex-1">{p.about ?? <DiscordText text={p.description} now={now} />}</div>
        <Count count={p.aboutCount} />
      </div>
      {post && post.fields.length > 0 && (
        <div className={`mt-2 grid gap-2 ${narrow ? 'grid-cols-1' : 'grid-cols-3'}`}>
          {post.fields.map((f) => (
            <div key={f.name} className={f.inline || narrow ? undefined : 'col-span-3'}>
              <div className="text-[14px] leading-[18px] font-semibold" style={{ color: TITLE }}>{f.name}</div>
              <div className="text-[14px] leading-[18px] [overflow-wrap:anywhere]">
                <DiscordText text={f.value} now={now} />
              </div>
            </div>
          ))}
        </div>
      )}
      <Cover p={p} className="mt-4 aspect-[2.5/1] w-full" rounded="rounded" />
      {post?.footer && (
        <div className="mt-2 flex items-center gap-2 text-[12px]" style={{ color: TEXT }}>
          <ServerIcon size={20} /> {post.footer}
        </div>
      )}
    </div>
  )
}

function PostButtons({ post }: { post?: Post | null }) {
  if (!post || post.buttons.length === 0) return null
  return (
    <div className="mt-1 flex flex-wrap gap-2">
      {post.buttons.map((b) => (
        <span key={b.url} className="inline-flex h-8 items-center gap-1.5 rounded-lg px-4 text-[14px] font-medium text-white" style={{ background: '#4e5058' }}>
          {b.label} <ExternalLink className="size-4" />
        </span>
      ))}
    </div>
  )
}
