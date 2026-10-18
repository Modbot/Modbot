import { PLATFORM_ICONS } from '@/lib/platformIcons'

export function PlatformIcons({ platforms, size = 28 }: { platforms: string[]; size?: number }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      {platforms.map((p) => {
        const icon = PLATFORM_ICONS[p]
        if (!icon) return null
        return (
          <span
            key={p}
            className="inline-grid place-items-center rounded-full border-2"
            style={{ width: size, height: size, borderColor: icon.colour }}
          >
            <svg viewBox={icon.viewBox} style={{ width: size * 0.5, height: size * 0.5 }} fill={icon.colour}>
              <path d={icon.d} />
            </svg>
          </span>
        )
      })}
    </span>
  )
}
