import { useState } from 'react'
import { PlatformIcons } from './PlatformIcons'
// Design mock-up: the event the way VRChat shows it, in game and on vrchat.com.

type Props = {
  title: string
  description: string
  picture: string | null
  category: string
  /** Every VRChat category, value to name; when set, the tag is a dropdown. */
  categoryOptions?: Record<string, string>
  onCategory?: (value: string) => void
  featured: boolean
  startsAt: string
  endsAt: string
  publicEvent: boolean
  platforms: string[]
  /** The box the description is typed in, drawn where VRChat shows it. */
  about?: React.ReactNode
  /** The box the title is typed in. */
  titleBox?: React.ReactNode
  /** The description's count, "12 / 1000", red once over. */
  aboutCount?: { label: string; over: boolean }
  /** The event's languages and tags; with a setter, the website event page edits them. */
  languages?: string[]
  /** The event repeats: the website shows a repeat mark and a link to the series. */
  repeats?: boolean
  onLanguages?: (list: string[]) => void
  eventTags?: string[]
  onEventTags?: (list: string[]) => void
}

// VRChat's default colours, measured on vrchat.com: .agent/research/2026-10-15-vrchat-colors.md.
// In game is not measured yet; the website teal stands in for it.
const GAME_CARD = '#064b5c'
const ACCENT = '#6ae3f9'
const WEB_BG = '#181b1f'
const WEB_CARD = '#252a30'
const TEAL = '#064b5c'
const WEB_TEXT = '#f8f9fa'
const WEB_MUTED = '#9c9c9c'

const time = (iso: string) => new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
const longDay = (iso: string) => new Date(iso).toLocaleDateString(undefined, { month: 'long', day: 'numeric', year: 'numeric' })
const shortDay = (iso: string) => new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })

function Picture({ src, className, children, platforms = [] }: { src: string | null; className?: string; children?: React.ReactNode; platforms?: string[] }) {
  return (
    <div
      className={`relative overflow-hidden bg-neutral-700 bg-cover bg-center ${className ?? ''}`}
      style={src ? { backgroundImage: `url(${src})` } : undefined}
    >
      {platforms.length > 0 && (
        <span className="absolute right-1.5 top-1.5"><PlatformIcons platforms={platforms} size={18} /></span>
      )}
      {children}
    </div>
  )
}

function Tag({ children, tone, className, web }: { children: React.ReactNode; tone: 'white' | 'gold' | 'green'; className?: string; web?: boolean }) {
  const look = {
    white: 'bg-white text-[#252a30]',
    gold: 'bg-[#b8860b] text-white',
    green: 'bg-[#2e8b57] text-white',
  }[tone]
  return <span className={`inline-block font-bold ${web ? 'rounded-md px-2 text-[16px] leading-6' : 'rounded-[4px] px-[5px] text-[10px] leading-[14px]'} ${look} ${className ?? ''}`}>{children}</span>
}

function GroupIcon({ size = 28 }: { size?: number }) {
  return <span className="inline-block shrink-0 rounded-full border-2 border-[#b8860b] bg-neutral-600" style={{ width: size, height: size }} />
}

const VIEWS = [
  'In game: group events',
  'In game: calendar',
  'In game: event page',
  'In game: live now',
  'In game: notification',
  'Website: group events tab',
  'Website: event page',
  'Website: calendar',
  'Website: calendar popup',
] as const
type View = (typeof VIEWS)[number]

// Kept while the page is open, so switching form tabs keeps the view.
let lastViews: View[] = ['Website: event page']
let lastScale = 1

/** Draws at real size, then shrinks the whole thing, so every ratio stays VRChat's. */
function Scaled({ by, children }: { by: number; children: React.ReactNode }) {
  return <div style={{ zoom: by }}>{children}</div>
}

function Shown({ name, view, children }: { name: View; view: View[]; children: React.ReactNode }) {
  if (!view.includes(name)) return null
  return <div className="max-w-full overflow-x-auto">{children}</div>
}

export function VRChatPreviews(p: Props) {
  const [view, setViewState] = useState<View[]>(lastViews)
  const setView = (v: View) => {
    const next = view.includes(v) ? view.filter((x) => x !== v) : VIEWS.filter((x) => x === v || view.includes(x))
    lastViews = next
    setViewState(next)
  }
  const [scale, setScaleState] = useState(lastScale)
  const setScale = (s: number) => {
    lastScale = s
    setScaleState(s)
  }
  const title = p.title || 'Untitled event'
  const tags = (
    <>
      {p.featured && <Tag tone="gold">Featured</Tag>}
      <CategoryTag {...p} />
    </>
  )
  const visibility = p.publicEvent ? 'Public' : 'Group'

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
      <div className="overflow-auto rounded-sm border border-(length:--hairline) border-input p-2.5">
      <div className="flex flex-wrap items-start gap-3" style={{ zoom: scale }}>
      {/* In-game views: sizes and ratios measured from VRChat screenshots (kept outside the repo); colours are a guess. */}
      <Shown view={view} name="In game: group events">
        <div className="w-[143px] overflow-hidden rounded-md" style={{ background: GAME_CARD }}>
          <Picture src={p.picture} className="h-[85px]">
            <div className="absolute left-1 top-1 flex flex-col items-start gap-0.5">{tags}</div>
            <span className="absolute right-1 bottom-1"><GroupIcon size={32} /></span>
          </Picture>
          <div className="px-2 pt-1.5 pb-2">
            <div className="truncate text-[11px] font-bold" style={{ color: ACCENT }}>Your group</div>
            <div className="truncate text-xs font-bold text-white">{title}</div>
            <div className="mt-2 text-[11px] text-white/60">{time(p.startsAt)}</div>
          </div>
        </div>
      </Shown>

      <Shown view={view} name="In game: calendar">
        <div className="w-[236px] overflow-hidden rounded-md" style={{ background: GAME_CARD }}>
          <Picture src={p.picture} className="h-[95px]">
            <div className="absolute left-1 top-1 flex flex-col items-start gap-0.5">{tags}</div>
            <span className="absolute right-1 bottom-[-12px]"><GroupIcon size={45} /></span>
          </Picture>
          <div className="px-2.5 pt-2 pb-2.5 text-white">
            <div className="line-clamp-2 pr-12 text-[13px] font-bold">{title}</div>
            <div className="mt-1 text-[11px] text-white/60">
              {time(p.startsAt)} - {time(p.endsAt)}
            </div>
          </div>
        </div>
      </Shown>

      <Shown view={view} name="In game: event page">
        <div className="w-[612px]" style={{ color: ACCENT }}>
          <Picture src={p.picture} className="h-[305px] rounded-md">
            <div className="absolute inset-x-0 bottom-0 flex h-[100px] flex-col justify-end bg-black/60 px-4 pb-3">
              <div className="flex gap-1"><CategoryTag {...p} /></div>
              <div className="mt-1 truncate text-[22px] font-bold text-white">{p.titleBox ?? title}</div>
              <div className="text-[15px] font-bold">
                {longDay(p.startsAt)} {time(p.startsAt)} - {time(p.endsAt)}
              </div>
            </div>
          </Picture>
          <div className="mt-[11px] grid grid-cols-[205px_1fr] gap-[11px]">
            <div className="self-start rounded-md p-4" style={{ background: GAME_CARD }}>
              <div className="text-[22px] font-bold">Details</div>
              <div className="text-[15px] font-semibold">0 People Are Interested</div>
            </div>
            <div className="rounded-md p-4" style={{ background: GAME_CARD }}>
              <div className="flex items-baseline justify-between gap-2">
                <div className="text-[22px] font-bold">About This Event</div>
                {p.aboutCount && (
                  <span className={`font-mono text-xs ${p.aboutCount.over ? 'text-red-400' : 'opacity-60'}`}>{p.aboutCount.label}</span>
                )}
              </div>
              <div className="text-[15px] font-semibold whitespace-pre-wrap">{p.about ?? p.description}</div>
            </div>
          </div>
        </div>
      </Shown>

      <Shown view={view} name="In game: live now">
        <Picture src={p.picture} className="h-[205px] w-[661px] rounded-xl">
          <div className="absolute inset-0 flex flex-col justify-between bg-gradient-to-t from-black/70 via-transparent to-transparent p-5 text-white">
            <span className="self-start rounded-md bg-[#e53935] px-3 py-1 text-lg font-black italic">LIVE</span>
            <div>
              <div className="truncate text-[28px] font-bold italic">{title}</div>
              <div className="text-[15px] font-semibold">
                {longDay(p.startsAt)} at {time(p.startsAt)}
              </div>
            </div>
          </div>
        </Picture>
      </Shown>

      <Shown view={view} name="In game: notification">
        <div className="flex w-[612px] gap-4 rounded-md p-3" style={{ background: GAME_CARD }}>
          <Picture src={p.picture} className="h-[86px] w-[152px] shrink-0 rounded">
            <span className="absolute left-1 top-1"><GroupIcon size={22} /></span>
          </Picture>
          <div className="min-w-0 flex-1">
            <div className="truncate text-[15px] font-bold" style={{ color: ACCENT }}>
              New event by Your group: {title}!
            </div>
            <div className="mt-2 truncate text-[15px] font-bold text-white">{p.description.split('\n')[0]}</div>
            <div className="mt-2 text-xs text-white/60">Just now</div>
          </div>
        </div>
      </Shown>

      {/* Website views: drawn at vrchat.com's own sizes (.agent/research/2026-10-15-vrchat-website-event-views.md), then shrunk as a whole. */}
      <Shown view={view} name="Website: group events tab">
        <Scaled by={0.48}>
          <div className="relative flex w-[1275px] gap-4 rounded-lg p-1 text-[16px]" style={{ background: WEB_CARD, color: WEB_TEXT }}>
            <Picture src={p.picture} className="h-[200px] w-[355px] shrink-0 rounded-md">
              <div className="absolute left-3 top-3 flex flex-col items-start gap-1">
                <CategoryTag {...p} web />
                {p.featured && <Tag tone="gold" web>Featured</Tag>}
              </div>
            </Picture>
            <div className="flex min-w-0 flex-1 flex-col py-3 pr-[210px]">
              <div className="truncate text-xl font-bold text-white">{p.titleBox ?? title}</div>
              <div className="mt-2 line-clamp-[8] whitespace-pre-wrap">{p.description}</div>
              <div className="mt-auto flex items-center gap-2 pt-4" style={{ color: WEB_MUTED }}>
                {p.repeats ? '⟳' : '◷'} {shortDay(p.startsAt)} at {time(p.startsAt)} | {visibility} | <PlatformIcons platforms={p.platforms} size={24} />
              </div>
            </div>
            <span className="absolute right-4 top-3 w-[178px] rounded border-2 py-[5px] text-center text-white" style={{ background: TEAL, borderColor: TEAL }}>View</span>
          </div>
        </Scaled>
      </Shown>

      <Shown view={view} name="Website: event page">
        <Scaled by={0.48}>
          <div className="w-[1310px] text-[16px] text-white">
            <Picture src={p.picture} className="h-[530px] rounded-t-lg">
              <div className="absolute inset-x-0 bottom-0 flex h-[200px] items-start justify-between p-5" style={{ background: 'rgba(17,17,17,0.9)' }}>
                <div className="min-w-0 max-w-[650px]">
                  <div className="flex gap-2">
                    <CategoryTag {...p} web />
                    {p.featured && <Tag tone="gold" web>Featured</Tag>}
                  </div>
                  <div className="text-xl font-thin">{shortDay(p.startsAt)}</div>
                  <div className="line-clamp-2 text-[30px] leading-9 font-bold">{p.titleBox ?? title}</div>
                  <div className="mt-1 flex items-center gap-2">
                    <span className="inline-block size-6 rounded-full border-2 border-[#1fd1ed] bg-neutral-600" />
                    <span className="text-[#1fd1ed]">Your group</span>
                    <span style={{ color: WEB_MUTED }}>| {visibility} |</span>
                    <PlatformIcons platforms={p.platforms} size={24} />
                  </div>
                </div>
                <span className="mt-6 w-[254px] shrink-0 rounded border-2 border-[#333] bg-[#333] py-[5px] text-center">Add to Calendar</span>
              </div>
            </Picture>
            <div className="mt-4 grid grid-cols-2 gap-4">
              <div>
                <div className="rounded-t-lg p-[14.4px] text-2xl font-bold" style={{ background: WEB_CARD }}>Details</div>
                <div className="rounded-b-lg p-5" style={{ background: WEB_BG }}>
                  <div className="font-bold">Date and Time</div>
                  <div className="pl-[43px]">{shortDay(p.startsAt)} at {time(p.startsAt)} - {time(p.endsAt)}</div>
                  <div className="pl-[43px] text-[#0e9bb1]">Export Event</div>
                  {p.repeats && (
                    <>
                      <div className="mt-6 font-bold">Repeating Event</div>
                      <div className="pl-[43px] text-[#0e9bb1]">View Event Series</div>
                    </>
                  )}
                  <div className="mt-6 font-bold">Interested People</div>
                  <div className="pl-[43px]">0</div>
                  <div className="mt-6 font-bold">Languages</div>
                  <div className="pl-[43px]"><Chips list={p.languages ?? []} onChange={p.onLanguages} adding="Add a language" /></div>
                  <div className="mt-6 font-bold">Tags</div>
                  <div className="pl-[43px]"><Chips list={p.eventTags ?? []} onChange={p.onEventTags} adding="Add a tag" /></div>
                </div>
              </div>
              <div>
                <div className="flex items-baseline justify-between gap-2 rounded-t-lg p-[14.4px]" style={{ background: WEB_CARD }}>
                  <span className="text-2xl font-bold">About This Event</span>
                  {p.aboutCount && (
                    <span className={`font-mono text-[16px] ${p.aboutCount.over ? 'text-red-400' : 'opacity-60'}`}>{p.aboutCount.label}</span>
                  )}
                </div>
                <div className="rounded-b-lg p-[14.4px] whitespace-pre-wrap" style={{ background: WEB_BG }}>{p.about ?? p.description}</div>
              </div>
            </div>
          </div>
        </Scaled>
      </Shown>

      <Shown view={view} name="Website: calendar">
        <div className="h-[171px] w-[181px] border p-1 text-white" style={{ background: WEB_BG, borderColor: '#2d363f' }}>
          <div className="size-[38px] p-1 text-[25.6px] leading-[30px] font-light">{new Date(p.startsAt).getDate()}</div>
          <div className="flex h-[26px] items-center gap-1.5 overflow-hidden rounded-lg py-0.5 pl-1 text-sm font-bold whitespace-nowrap">
            <span className="size-2 shrink-0 rounded-full" style={{ background: TEAL }} />
            {title}
          </div>
        </div>
      </Shown>

      <Shown view={view} name="Website: calendar popup">
        <div className="relative w-[340px] rounded-lg p-1 text-[16px] shadow-[0_2px_6px_rgba(0,0,0,0.15)]" style={{ background: WEB_CARD }}>
          <Picture src={p.picture} className="h-[187px] w-[332px] rounded-md">
            <div className="absolute left-3 top-3 flex flex-col items-start gap-1">
              <CategoryTag {...p} web />
              {p.featured && <Tag tone="gold" web>Featured</Tag>}
            </div>
            <span className="absolute right-3 top-3 flex size-9 items-center justify-center rounded border-2 border-[#333] bg-[#333] text-white">✕</span>
          </Picture>
          <div className="px-3 pb-3">
            <div className="mt-2 truncate text-xl font-bold text-white">{p.titleBox ?? title}</div>
            <div className="mt-1 flex items-center gap-1 text-white">
              <span className="inline-block size-8 rounded-full bg-[#6ae3f9]" /> Your group
            </div>
            <div className="mt-6" style={{ color: WEB_MUTED }}>
              ◷ {shortDay(p.startsAt)} at {time(p.startsAt)} | {visibility}
            </div>
            <div className="mt-2 rounded border-2 py-[5px] text-center text-white" style={{ background: TEAL, borderColor: TEAL }}>View</div>
          </div>
        </div>
      </Shown>
      </div>
      </div>
    </div>
  )
}

function CategoryTag(p: Props & { web?: boolean }) {
  const options = p.categoryOptions
  if (!options || !p.onCategory) return <Tag tone="white" web={p.web}>{options?.[p.category] ?? p.category}</Tag>
  // Looks exactly like the in-game tag; the dropdown sits on top of it, unseen.
  return (
    <span className="group relative inline-block cursor-pointer">
      <Tag tone="white" web={p.web} className="transition-colors group-hover:bg-neutral-300">{options[p.category] ?? p.category}</Tag>
      <select
        value={p.category}
        onChange={(e) => p.onCategory!(e.target.value)}
        aria-label="VRChat category"
        className="absolute inset-0 cursor-pointer opacity-0"
      >
        {Object.entries(options).map(([value, label]) => (
          <option key={value} value={value} className="bg-neutral-800 text-white">{label}</option>
        ))}
      </select>
    </span>
  )
}

/** VRChat's website chips (measured: 32 high, 2px corners, 4px 12px padding, 8px apart); with onChange they can be removed and added. */
function Chips({ list, onChange, adding }: { list: string[]; onChange?: (list: string[]) => void; adding: string }) {
  const [draft, setDraft] = useState('')
  const add = () => {
    const value = draft.trim()
    if (value && !list.includes(value)) onChange?.([...list, value])
    setDraft('')
  }
  return (
    <div className="mt-1 flex flex-wrap gap-2">
      {list.map((item) => (
        <span key={item} className="inline-flex h-8 items-center gap-2 rounded-[2px] px-3 py-1" style={{ background: 'rgba(24,27,31,0.8)', boxShadow: 'inset 0 0 0 1px #2d363f' }}>
          {item}
          {onChange && (
            <button type="button" aria-label={`Remove ${item}`} onClick={() => onChange(list.filter((x) => x !== item))} className="opacity-60 hover:opacity-100">
              ✕
            </button>
          )}
        </span>
      ))}
      {onChange && (
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ',') {
              e.preventDefault()
              add()
            }
          }}
          onBlur={add}
          placeholder={adding}
          aria-label={adding}
          className="h-8 w-40 rounded-[2px] bg-transparent px-3 outline-none placeholder:text-white/40"
          style={{ boxShadow: 'inset 0 0 0 1px #2d363f' }}
        />
      )}
    </div>
  )
}
