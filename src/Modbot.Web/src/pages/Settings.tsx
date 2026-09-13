import { useCallback, useEffect, useState } from 'react'
import { DataSection } from '@/components/settings/DataSection'
import { EvidenceSection } from '@/components/settings/EvidenceSection'
import { IntegrationsSection } from '@/components/settings/IntegrationsSection'
import { SyncSection } from '@/components/settings/SyncSection'
import { VRChatSection } from '@/components/settings/VRChatSection'
import { api, type OnboardingStatus } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * The sections, in page order. Each id is also the `<section id>` its component renders, which
 * is what the nav scrolls to and what `/settings#evidence` lands on. Adding a section means one
 * entry here and one component in the list below.
 */
const SECTIONS = [
  { id: 'data', label: 'Data' },
  { id: 'vrchat', label: 'VRChat account' },
  { id: 'integrations', label: 'Integrations' },
  { id: 'evidence', label: 'Evidence' },
  { id: 'sync', label: 'Sync' },
] as const

type SectionId = (typeof SECTIONS)[number]['id']

/**
 * Settings.
 *
 * One page of sections rather than tabs. Each section is a grid of cards, and a grid with one
 * or two cards on it — which is what most tabs held — looks emptier than the full-width forms
 * did. Laid end to end the cards keep each other company, an operator looking for "the proxy
 * setting" scrolls or clicks rather than guessing which tab it is under, and the next feature
 * adds a section without deciding whether it deserves a tab.
 *
 * Three of the sections are the onboarding wizard's own steps, re-run in place. That is not a
 * convenience — spec 7.1 designed each step as an independently re-runnable slice for exactly this
 * reason, so a deployment whose host became WAF-blocked in March re-runs the connection check
 * rather than the wizard. Duplicating the logic here would give Modbot two implementations of
 * "store a VRChat credential", and the second one would be the one that drifts.
 */
export function Settings() {
  const [status, setStatus] = useState<OnboardingStatus | null>(null)

  const refresh = useCallback(
    () => api.onboardingStatus().then(setStatus).catch(() => setStatus(null)),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  return (
    <div className="mx-auto w-full max-w-6xl lg:grid lg:grid-cols-[9rem_minmax(0,1fr)] lg:gap-8">
      <SectionNav />

      <div className="flex min-w-0 flex-col gap-10">
        <DataSection />
        <VRChatSection status={status} refresh={refresh} />
        <IntegrationsSection status={status} refresh={refresh} />
        <EvidenceSection />
        <SyncSection />
      </div>
    </div>
  )
}

/**
 * A row of links above the sections on narrow screens, a sticky column beside them on wide
 * ones. The highlighted entry follows the scroll position, so it doubles as "where am I" on a
 * page this long.
 */
function SectionNav() {
  const [active, setActive] = useState<SectionId>(SECTIONS[0].id)

  useEffect(() => {
    // The band is the top third of the viewport below the topbar, and the highlighted section is
    // the first in page order with anything inside it. Tracked as a set rather than "the last to
    // enter", because sections grow as their data arrives and a section that was in the band
    // while the one above it was still a loading placeholder must not stay highlighted after
    // it has been pushed out.
    const visible = new Set<string>()
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) visible.add(entry.target.id)
          else visible.delete(entry.target.id)
        }
        const first = SECTIONS.find((s) => visible.has(s.id))
        if (first) setActive(first.id)
      },
      { rootMargin: '-80px 0px -66% 0px' },
    )

    for (const s of SECTIONS) {
      const element = document.getElementById(s.id)
      if (element) observer.observe(element)
    }

    // A pasted /settings#evidence lands on the section it names.
    const wanted = window.location.hash.slice(1)
    if (SECTIONS.some((s) => s.id === wanted)) {
      document.getElementById(wanted)?.scrollIntoView()
    }

    return () => observer.disconnect()
  }, [])

  const go = (id: SectionId) => {
    setActive(id)
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth' })
  }

  return (
    <nav
      aria-label="Settings sections"
      className="mb-4 flex flex-wrap gap-1 lg:sticky lg:top-20 lg:mb-0 lg:flex-col lg:self-start"
    >
      {SECTIONS.map((s) => (
        <button
          key={s.id}
          type="button"
          aria-current={active === s.id ? 'true' : undefined}
          onClick={() => go(s.id)}
          className={cn(
            'rounded-md px-2.5 text-left font-medium transition-colors',
            active === s.id
              ? 'bg-accent text-accent-foreground'
              : 'text-muted-foreground hover:bg-secondary hover:text-foreground',
          )}
          style={{ fontSize: 'var(--text-small)', height: 'var(--control-h)' }}
        >
          {s.label}
        </button>
      ))}
    </nav>
  )
}
