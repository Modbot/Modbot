import { useEffect, useState } from 'react'
import { api, type GateHealth } from '@/lib/api'
import { DOT, postureOf, TONE } from '@/lib/gate'
import { cn } from '@/lib/utils'

/**
 * The sidebar's gate indicator.
 *
 * The design prototype has a green "VRChat · healthy" dot here and it was removed rather than
 * ported, because a hardcoded green dot is worse than no dot at all: this is the one place an
 * operator looks to find out that something is wrong. It is back now that a real endpoint answers
 * it — and it reports a *posture* rather than a colour, because "rate limited" and "WAF blocked"
 * are both "not green" and want opposite responses.
 *
 * A failed fetch shows as unknown, never as healthy. Optimism here is the same lie in a quieter
 * costume.
 */
export function GateIndicator({ onOpen }: { onOpen?: () => void }) {
  const [gate, setGate] = useState<GateHealth | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false

    const load = () =>
      api
        .gateHealth()
        .then((next) => {
          if (cancelled) return
          setGate(next)
          setFailed(false)
        })
        .catch(() => {
          if (!cancelled) setFailed(true)
        })

    void load()
    const timer = setInterval(() => void load(), 30_000)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  if (failed || !gate) {
    return (
      <Shell onOpen={onOpen} title={failed ? 'Could not reach the Modbot server.' : undefined}>
        <span className="size-1.5 shrink-0 rounded-full bg-muted-foreground" />
        <span className="truncate text-muted-foreground">
          VRChat · {failed ? 'unknown' : 'checking…'}
        </span>
      </Shell>
    )
  }

  const posture = postureOf(gate.posture)

  return (
    <Shell onOpen={onOpen} title={gate.headline}>
      <span className={cn('size-1.5 shrink-0 rounded-full', DOT[posture.tone])} />
      <span className={cn('truncate', posture.tone === 'ok' ? 'text-muted-foreground' : TONE[posture.tone])}>
        VRChat · {posture.label.toLowerCase()}
      </span>
    </Shell>
  )
}

function Shell({
  onOpen,
  title,
  children,
}: {
  onOpen?: () => void
  title?: string
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      onClick={onOpen}
      title={title}
      className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left hover:bg-secondary"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {children}
    </button>
  )
}
