import { Monitor, Smartphone } from 'lucide-react'

/**
 * The marks the game draws on an instance: which platforms the world runs on, and where the
 * instance is hosted.
 *
 * Drawn here rather than copied from VRChat, whose own icon artwork is theirs to license. The
 * badges keep the game's colours -- blue PC, green Android, white iOS -- so they read the same at
 * a glance. Flags are drawn as small pictures because Windows shows flag emoji as two letters.
 */

const PLATFORMS: Record<string, { name: string; className: string; Icon: typeof Monitor }> = {
  standalonewindows: { name: 'PC', className: 'bg-[#1a8fe3] text-white', Icon: Monitor },
  android: { name: 'Android', className: 'bg-[#3ddc84] text-black', Icon: Smartphone },
  ios: { name: 'iOS', className: 'bg-white text-black', Icon: Smartphone },
}

/** The order the game shows them in. */
const ORDER = ['standalonewindows', 'android', 'ios']

/** One round badge a platform, in the game's order. Platforms this build does not know are left out. */
export function PlatformBadges({ platforms }: { platforms: string[] | null }) {
  const known = ORDER.filter((p) => platforms?.includes(p))
  if (known.length === 0) return null

  return (
    <span className="flex gap-1">
      {known.map((p) => {
        const { name, className, Icon } = PLATFORMS[p]
        return (
          <span
            key={p}
            title={name}
            className={`flex size-5 items-center justify-center rounded-full ring-1 ring-black/40 ${className}`}
          >
            <Icon aria-hidden className="size-3" strokeWidth={2.5} />
            <span className="sr-only">{name}</span>
          </span>
        )
      })}
    </span>
  )
}

/**
 * Where the instance is hosted, the way the game says it: a flag, and for the United States a
 * letter for the coast.
 */
const REGIONS: Record<string, { name: string; flag: 'us' | 'eu' | 'jp'; letter?: string }> = {
  us: { name: 'US West', flag: 'us', letter: 'W' },
  use: { name: 'US East', flag: 'us', letter: 'E' },
  eu: { name: 'Europe', flag: 'eu' },
  jp: { name: 'Japan', flag: 'jp' },
}

export function RegionBadge({ region }: { region: string | null }) {
  if (!region) return null

  const known = REGIONS[region]
  if (!known)
    return <span className="font-semibold">{region.toUpperCase()}</span>

  return (
    <span title={known.name} className="flex items-center gap-1 font-semibold">
      <Flag which={known.flag} />
      {known.letter}
      <span className="sr-only">{known.name}</span>
    </span>
  )
}

/** A flag, small enough to sit beside a letter. Simplified: at this size stars and stripes blur. */
function Flag({ which }: { which: 'us' | 'eu' | 'jp' }) {
  return (
    <svg aria-hidden viewBox="0 0 30 20" className="h-3 w-[1.125rem] shrink-0 rounded-[2px] ring-1 ring-black/30">
      {which === 'us' && (
        <>
          <rect width="30" height="20" fill="#fff" />
          {[0, 2, 4, 6, 8, 10, 12].map((i) => (
            <rect key={i} y={(i * 20) / 13} width="30" height={20 / 13} fill="#b22234" />
          ))}
          <rect width="13" height={(7 * 20) / 13} fill="#3c3b6e" />
        </>
      )}
      {which === 'eu' && (
        <>
          <rect width="30" height="20" fill="#039" />
          {Array.from({ length: 12 }, (_, i) => {
            const a = (i * Math.PI) / 6
            return <circle key={i} cx={15 + 6 * Math.sin(a)} cy={10 - 6 * Math.cos(a)} r="1" fill="#fc0" />
          })}
        </>
      )}
      {which === 'jp' && (
        <>
          <rect width="30" height="20" fill="#fff" />
          <circle cx="15" cy="10" r="6" fill="#bc002d" />
        </>
      )}
    </svg>
  )
}
