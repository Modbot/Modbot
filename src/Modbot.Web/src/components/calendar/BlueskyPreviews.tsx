import { useRef, useState, type ReactNode, type RefObject } from 'react'
import { Film, Globe, Image as ImageIcon, Loader2, Smile, X } from 'lucide-react'
import { BLUESKY_ALT_MAX, BLUESKY_PICTURES_MAX } from '@/lib/blueskyPictures'
import { EmojiPicker } from './EmojiPicker'
// Design mock-up: Bluesky's New post box, in its dim theme. Not measured: its colours and sizes as
// drawn by its web app, by eye.

const APP_BG = '#161e27'
const LINE = '#2e4052'
const TEXT = '#ffffff'
const MUTED = '#aebbc9'
const LINK = '#208bfe'
const AVATAR = '#208bfe'

/** One picture of the post as the box shows it. */
export type PostPicture = { id: string; url: string; alt: string }

type Props = {
  title: string
  /** The box the title is typed in, drawn as the post's first line. */
  titleBox?: ReactNode
  /** The box the description is typed in. */
  about?: ReactNode
  /** The lines the server puts between the title and the description, and after it (time, world, link). */
  before: string
  after: string
  /** The post's pictures, first first. */
  pictures: PostPicture[]
  /** Pictures dropped or picked; the form keeps as many as there is room for. */
  onAddFiles: (files: File[]) => void
  onAlt: (index: number, alt: string) => void
  onRemove: (index: number) => void
  /** A picture is on its way up. */
  uploading: boolean
  /** Why the last picture was not added. */
  error: string | null
  /** An emoji picked; the form puts it at the caret of the box last typed in. */
  onEmoji: (emoji: string) => void
  /** The link card, when the post has a link and no picture. */
  card: { uri: string; title: string; description: string; host: string } | null
  /** The whole post's count, "120 / 300", red once over. */
  count?: { label: string; over: boolean }
}

// Kept while the page is open, so switching form tabs keeps the size.
let lastScale = 1

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

function imagesOf(files: FileList | null): File[] {
  return Array.from(files ?? []).filter((f) => f.type.startsWith('image/'))
}

/** The pictures (each with its ALT and remove buttons), or the link card, or an empty spot to drop or pick a picture. */
function Media({
  p,
  file,
  altOpen,
  onAltOpen,
}: {
  p: Props
  file: RefObject<HTMLInputElement | null>
  altOpen: number | null
  onAltOpen: (index: number | null) => void
}) {
  const [over, setOver] = useState(false)
  const full = p.pictures.length >= BLUESKY_PICTURES_MAX
  const dropping = over && !full

  return (
    <div
      className="mt-2"
      onDragOver={(e) => {
        e.preventDefault()
        setOver(true)
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => {
        e.preventDefault()
        setOver(false)
        const files = imagesOf(e.dataTransfer.files)
        if (files.length > 0) p.onAddFiles(files)
      }}
    >
      {p.pictures.length === 0 && !p.uploading && p.card ? (
        <div className="overflow-hidden rounded-xl border" style={{ borderColor: dropping ? LINK : LINE }}>
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
      ) : p.pictures.length === 0 && !p.uploading ? (
        <button
          type="button"
          onClick={() => file.current?.click()}
          className="flex h-40 w-full items-center justify-center rounded-xl border text-[14px] font-semibold"
          style={{ borderColor: dropping ? LINK : LINE, background: 'rgba(174,187,201,0.08)', color: TEXT }}
        >
          Add a picture
        </button>
      ) : (
        <div className={`grid gap-1 overflow-hidden rounded-xl ${p.pictures.length + (p.uploading ? 1 : 0) > 1 ? 'grid-cols-2' : 'grid-cols-1'}`}>
          {p.pictures.map((picture, i) => (
            <div key={picture.id} className="relative overflow-hidden rounded-sm" style={{ background: 'rgba(174,187,201,0.08)' }}>
              <img
                src={picture.url}
                alt={picture.alt}
                className={`block w-full object-cover ${p.pictures.length === 1 && !p.uploading ? 'max-h-[420px]' : 'h-48'}`}
              />
              <button
                type="button"
                aria-label={`Alt text for picture ${i + 1}`}
                aria-pressed={altOpen === i}
                onClick={() => onAltOpen(altOpen === i ? null : i)}
                className="absolute bottom-2 left-2 rounded px-1.5 py-0.5 text-[11px] font-bold"
                style={{ background: altOpen === i ? LINK : 'rgba(0,0,0,0.75)', color: TEXT }}
              >
                {picture.alt ? 'ALT' : '+ ALT'}
              </button>
              <button
                type="button"
                aria-label={`Remove picture ${i + 1}`}
                onClick={() => {
                  onAltOpen(null)
                  p.onRemove(i)
                }}
                className="absolute top-2 right-2 flex size-7 items-center justify-center rounded-full"
                style={{ background: 'rgba(0,0,0,0.75)', color: TEXT }}
              >
                <X className="size-4" />
              </button>
            </div>
          ))}
          {p.uploading && (
            <div className="flex h-48 items-center justify-center rounded-sm" style={{ background: 'rgba(174,187,201,0.08)', color: MUTED }}>
              <Loader2 className="size-5 animate-spin" aria-label="Uploading" />
            </div>
          )}
        </div>
      )}
      {altOpen !== null && p.pictures[altOpen] && (
        <textarea
          aria-label={`Alt text for picture ${altOpen + 1}`}
          value={p.pictures[altOpen].alt}
          maxLength={BLUESKY_ALT_MAX}
          rows={3}
          autoFocus
          placeholder={p.title}
          onChange={(e) => p.onAlt(altOpen, e.target.value)}
          className="mt-2 block w-full resize-y rounded-lg border bg-transparent px-3 py-2 text-[15px] leading-5 outline-none placeholder:opacity-50 focus:border-[#208bfe]"
          style={{ borderColor: LINE, color: TEXT }}
        />
      )}
    </div>
  )
}

/** The post's words: the title line, the server's lines, the description, the server's lines after it. */
function Words({ p }: { p: Props }) {
  return (
    <div className="flex flex-col text-[18px] leading-6 whitespace-pre-wrap [overflow-wrap:anywhere]">
      <div>{p.titleBox ?? p.title}</div>
      {p.before && <div><Linked text={p.before} /></div>}
      {p.about && <div>{p.about}</div>}
      {p.after && <div><Linked text={p.after} /></div>}
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

export function BlueskyPreviews(p: Props) {
  const [scale, setScaleState] = useState(lastScale)
  const setScale = (s: number) => {
    lastScale = s
    setScaleState(s)
  }
  const file = useRef<HTMLInputElement>(null)
  // The picture whose alt text box is open.
  const [altOpen, setAltOpen] = useState<number | null>(null)
  const full = p.pictures.length >= BLUESKY_PICTURES_MAX

  return (
    <div className="flex flex-col gap-2">
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
        <div
          style={{ zoom: scale, color: TEXT, fontFamily: 'InterVariable, Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif' }}
        >
          {/* Bluesky's New post box, as the post would be written there. */}
          <div className="w-[600px] overflow-hidden rounded-xl border" style={{ borderColor: LINE, background: APP_BG }}>
            <div className="flex gap-2.5 px-4 pt-3 pb-3">
              <Avatar size={42} />
              <div className="min-w-0 flex-1 pt-2">
                <Words p={p} />
                <Media p={p} file={file} altOpen={altOpen} onAltOpen={setAltOpen} />
              </div>
            </div>
            <div className="px-4 pb-2">
              <span className="inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-[13px]" style={{ background: 'rgba(174,187,201,0.1)', color: MUTED }}>
                <Globe className="size-3.5" /> Anyone can interact
              </span>
            </div>
            <div className="flex items-center justify-between border-t px-3 py-2" style={{ borderColor: LINE, color: LINK }}>
              <span className="flex items-center gap-4">
                <button
                  type="button"
                  aria-label="Add pictures"
                  disabled={full || p.uploading}
                  onClick={() => file.current?.click()}
                  className="disabled:opacity-40"
                >
                  <ImageIcon className="size-5" />
                </button>
                <Film className="size-5" />
                <span className="rounded border px-0.5 text-[11px] font-bold" style={{ borderColor: LINK }}>GIF</span>
                <EmojiPicker onPick={p.onEmoji}>
                  <button type="button" aria-label="Add emoji" onMouseDown={(e) => e.preventDefault()}>
                    <Smile className="size-5" />
                  </button>
                </EmojiPicker>
              </span>
              <span className="flex items-center gap-3 text-[13px]" style={{ color: MUTED }}>
                English
                {p.count && <Ring count={p.count} />}
              </span>
            </div>
          </div>
        </div>
      </div>
      {p.error && (
        <span role="alert" className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {p.error}
        </span>
      )}
      <input
        ref={file}
        type="file"
        accept="image/*"
        multiple
        className="hidden"
        onChange={(e) => {
          const files = imagesOf(e.target.files)
          if (files.length > 0) p.onAddFiles(files)
          e.target.value = ''
        }}
      />
    </div>
  )
}
