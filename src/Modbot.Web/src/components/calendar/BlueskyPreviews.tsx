import { useRef, useState, type ReactNode } from 'react'
import { Ellipsis, Film, Globe, Heart, Image as ImageIcon, MessageCircle, Pin, Repeat2, Smile } from 'lucide-react'
import { timeOfDay } from '@/lib/format'
// Design mock-up: the post the way Bluesky shows it. Not measured: Bluesky's dim theme and sizes as
// drawn by its web app, by eye.

const APP_BG = '#161e27'
const LINE = '#2e4052'
const TEXT = '#ffffff'
const MUTED = '#aebbc9'
const LINK = '#208bfe'
const AVATAR = '#208bfe'

type Props = {
  title: string
  /** The box the title is typed in, drawn as the post's first line. */
  titleBox?: ReactNode
  /** The box the description is typed in. */
  about?: ReactNode
  /** The lines the server puts between the title and the description, and after it (time, world, link). */
  before: string
  after: string
  /** A crop not uploaded yet; drawn in place of the picture link. */
  picture: ReactNode
  pictureUrl: string | null
  /** A picture dropped or picked on a preview's picture. */
  onPicture?: (file: Blob) => void
  /** The link card, when the post has a link and no picture. */
  card: { uri: string; title: string; description: string; host: string } | null
  handle: string | null
  displayName: string | null
  /** When the post goes out. */
  postAt: string
  /** The whole post's count, "120 / 300", red once over. */
  count?: { label: string; over: boolean }
}

const VIEWS = ['New post', 'In the feed', 'Post page', 'Pinned on profile'] as const
type View = (typeof VIEWS)[number]

// Kept while the page is open, so switching form tabs keeps the view.
let lastView: View[] = ['In the feed']
let lastScale = 1

/** Bluesky's own wording of a post's time: "Oct 17, 2026 at 7:00 PM". */
function blueskyWhen(iso: string) {
  // Bluesky's own wording of a day, not the console's.
  // oxlint-disable-next-line no-restricted-properties
  const day = new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
  return `${day} at ${timeOfDay(iso)}`
}

/** Text with its links and tags in Bluesky's blue. */
function Linked({ text }: { text: string }) {
  const parts = text.split(/((?:https?:\/\/|www\.)\S+|#[^\s#]+)/g)
  return (
    <>
      {parts.map((part, i) =>
        i % 2 === 1 ? (
          <span key={i} style={{ color: LINK }}>
            {part.replace(/^https?:\/\//, '').replace(/^(.{27}).{4,}$/, '$1...')}
          </span>
        ) : (
          <span key={i}>{part}</span>
        ),
      )}
    </>
  )
}

function Avatar({ size }: { size: number }) {
  return <span className="inline-block shrink-0 rounded-full" style={{ width: size, height: size, background: AVATAR }} />
}

/** The picture in full, or the link card, or an empty spot to drop or pick a picture. */
function Media({ p }: { p: Props }) {
  const file = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  const has = p.picture || p.pictureUrl

  if (!has && p.card) {
    return (
      <div className="mt-2 overflow-hidden rounded-xl border" style={{ borderColor: LINE }}>
        <div className="flex flex-col gap-1 px-3 py-2.5">
          <span className="text-[15px] leading-5 font-semibold [overflow-wrap:anywhere]">{p.card.title}</span>
          {p.card.description && (
            <span className="line-clamp-2 text-[13px] leading-[18px] [overflow-wrap:anywhere]">{p.card.description}</span>
          )}
          <span className="mt-1 flex items-center gap-1 border-t pt-1.5 text-[12px]" style={{ borderColor: LINE, color: MUTED }}>
            <Globe className="size-3" /> {p.card.host}
          </span>
        </div>
      </div>
    )
  }

  if (!has && !p.onPicture) return null
  return (
    <div
      className={`group relative mt-2 overflow-hidden rounded-xl border ${p.onPicture ? 'cursor-pointer' : ''} ${has ? '' : 'h-40'}`}
      style={{ borderColor: LINE, background: has ? undefined : 'rgba(174,187,201,0.08)' }}
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
      {p.picture ? (
        <div className="[&>*]:w-full [&_img]:w-full">{p.picture}</div>
      ) : (
        p.pictureUrl && <img src={p.pictureUrl} alt="" className="block max-h-[520px] w-full object-contain" />
      )}
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

/** The post's words: the title line, the server's lines, the description, the server's lines after it. */
function Words({ p, big }: { p: Props; big?: boolean }) {
  return (
    <div className={`flex flex-col whitespace-pre-wrap [overflow-wrap:anywhere] ${big ? 'text-[18px] leading-6' : 'text-[15px] leading-5'}`}>
      <div>{p.titleBox ?? p.title}</div>
      {p.before && <div><Linked text={p.before} /></div>}
      {p.about && <div>{p.about}</div>}
      {p.after && <div><Linked text={p.after} /></div>}
    </div>
  )
}

function Actions({ big }: { big?: boolean }) {
  const icon = big ? 'size-[22px]' : 'size-[18px]'
  return (
    <div className={`flex items-center justify-between pr-8 ${big ? 'mt-3 border-t pt-3' : 'mt-2'}`} style={{ color: MUTED, borderColor: LINE }}>
      <MessageCircle className={icon} />
      <Repeat2 className={icon} />
      <Heart className={icon} />
      <Ellipsis className={icon} />
    </div>
  )
}

/** Bluesky's count ring: characters left, filling as the post grows, red once over. */
function Ring({ count }: { count: { label: string; over: boolean } }) {
  const [used, limit] = count.label.split('/').map((n) => Number(n.replace(/\D/g, '')))
  const share = limit ? Math.min(used / limit, 1) : 0
  const r = 9
  const round = 2 * Math.PI * r
  return (
    <span className="flex items-center gap-1.5">
      <span className={count.over ? 'text-red-400' : ''}>{limit - used}</span>
      <svg width="22" height="22" viewBox="0 0 22 22" className="-rotate-90">
        <circle cx="11" cy="11" r={r} fill="none" stroke={LINE} strokeWidth="2.5" />
        <circle cx="11" cy="11" r={r} fill="none" stroke={count.over ? '#ef4444' : LINK} strokeWidth="2.5" strokeDasharray={`${share * round} ${round}`} />
      </svg>
    </span>
  )
}

function Name({ p }: { p: Props }) {
  return (
    <span className="flex min-w-0 items-baseline gap-1 text-[15px] leading-5">
      <span className="truncate font-bold">{p.displayName ?? p.handle ?? 'Your account'}</span>
      <span className="truncate" style={{ color: MUTED }}>@{p.handle ?? 'you.bsky.social'}</span>
      <span style={{ color: MUTED }}>· now</span>
    </span>
  )
}

function FeedPost({ p, pinned }: { p: Props; pinned?: boolean }) {
  return (
    <div className="w-[600px] border-b px-4 pt-3 pb-2" style={{ borderColor: LINE }}>
      {pinned && (
        <div className="mb-1 flex items-center gap-1.5 pl-[30px] text-[13px] font-semibold" style={{ color: MUTED }}>
          <Pin className="size-3.5" /> Pinned
        </div>
      )}
      <div className="flex gap-2.5">
        <Avatar size={42} />
        <div className="min-w-0 flex-1">
          <Name p={p} />
          <div className="mt-0.5">
            <Words p={p} />
          </div>
          <Media p={p} />
          <Actions />
        </div>
      </div>
    </div>
  )
}

export function BlueskyPreviews(p: Props) {
  const [view, setViewState] = useState<View[]>(lastView)
  const setView = (v: View) => {
    const next = view.includes(v) ? view.filter((x) => x !== v) : VIEWS.filter((x) => x === v || view.includes(x))
    lastView = next
    setViewState(next)
  }
  const [scale, setScaleState] = useState(lastScale)
  const setScale = (s: number) => {
    lastScale = s
    setScaleState(s)
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap gap-1.5">
        {VIEWS.map((v) => (
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
      <div className="flex flex-wrap items-center justify-between gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <label className="flex items-center gap-2">
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
        {p.count && <span className={`font-mono ${p.count.over ? 'text-destructive' : ''}`}>{p.count.label}</span>}
      </div>
      <div className="overflow-auto rounded-sm border border-(length:--hairline) border-input p-2.5" style={{ background: APP_BG }}>
        <div
          className="flex flex-wrap items-start gap-4"
          style={{ zoom: scale, color: TEXT, fontFamily: 'InterVariable, Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif' }}
        >
          {/* Bluesky's New post box, as the post would be written there. */}
          {view.includes('New post') && (
            <div className="w-[600px] overflow-hidden rounded-xl border" style={{ borderColor: LINE, background: APP_BG }}>
              <div className="flex items-center justify-between px-4 py-2.5">
                <span className="text-[16px]" style={{ color: LINK }}>Cancel</span>
                <span className="rounded-full px-4 py-1.5 text-[14px] font-semibold" style={{ background: LINK, color: TEXT }}>Post</span>
              </div>
              <div className="flex gap-2.5 px-4 pt-1 pb-3">
                <Avatar size={42} />
                <div className="min-w-0 flex-1 pt-2">
                  <Words p={p} big />
                  <Media p={p} />
                </div>
              </div>
              <div className="px-4 pb-2">
                <span className="inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-[13px]" style={{ background: 'rgba(174,187,201,0.1)', color: MUTED }}>
                  <Globe className="size-3.5" /> Anyone can interact
                </span>
              </div>
              <div className="flex items-center justify-between border-t px-3 py-2" style={{ borderColor: LINE, color: LINK }}>
                <span className="flex items-center gap-4">
                  <ImageIcon className="size-5" />
                  <Film className="size-5" />
                  <span className="rounded border px-0.5 text-[11px] font-bold" style={{ borderColor: LINK }}>GIF</span>
                  <Smile className="size-5" />
                </span>
                <span className="flex items-center gap-3 text-[13px]" style={{ color: MUTED }}>
                  English
                  {p.count && <Ring count={p.count} />}
                </span>
              </div>
            </div>
          )}

          {view.includes('In the feed') && <FeedPost p={p} />}

          {view.includes('Post page') && (
            <div className="w-[600px] px-4 pt-3 pb-2">
              <div className="flex items-center gap-2.5">
                <Avatar size={42} />
                <div className="flex min-w-0 flex-1 flex-col text-[15px] leading-5">
                  <span className="truncate font-bold">{p.displayName ?? p.handle ?? 'Your account'}</span>
                  <span className="truncate" style={{ color: MUTED }}>@{p.handle ?? 'you.bsky.social'}</span>
                </div>
                <span className="rounded-full px-4 py-1.5 text-[14px] font-semibold" style={{ background: LINK, color: TEXT }}>
                  + Follow
                </span>
              </div>
              <div className="mt-3">
                <Words p={p} big />
              </div>
              <Media p={p} />
              <div className="mt-3 text-[14px]" style={{ color: MUTED }}>
                {blueskyWhen(p.postAt)}
              </div>
              <Actions big />
            </div>
          )}

          {view.includes('Pinned on profile') && <FeedPost p={p} pinned />}
        </div>
      </div>
    </div>
  )
}
