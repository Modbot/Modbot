import { useCallback, useEffect, useState } from 'react'
import { DataSection } from '@/components/settings/DataSection'
import { EvidenceSection } from '@/components/settings/EvidenceSection'
import { IntegrationsSection } from '@/components/settings/IntegrationsSection'
import { Placeholder } from '@/components/settings/fields'
import { SyncSection } from '@/components/settings/SyncSection'
import { VRChatSection } from '@/components/settings/VRChatSection'
import { api, type OnboardingStatus } from '@/lib/api'
import { cn } from '@/lib/utils'

const TABS = [
  { id: 'data', label: 'Data' },
  { id: 'vrchat', label: 'VRChat account' },
  { id: 'integrations', label: 'Integrations' },
  { id: 'evidence', label: 'Evidence' },
  { id: 'sync', label: 'Sync' },
] as const

type TabId = (typeof TABS)[number]['id']

/**
 * Settings.
 *
 * Three of these tabs are the onboarding wizard's own steps, re-run in place. That is not a
 * convenience — spec 7.1 designed each step as an independently re-runnable slice for exactly this
 * reason, so a deployment whose host became WAF-blocked in March re-runs the connection check
 * rather than the wizard. Duplicating the logic here would give Modbot two implementations of
 * "store a VRChat credential", and the second one would be the one that drifts.
 *
 * Each tab's content lives in its own file under components/settings/.
 */
export function Settings() {
  const [tab, setTab] = useState<TabId>('data')
  const [status, setStatus] = useState<OnboardingStatus | null>(null)

  const refresh = useCallback(
    () => api.onboardingStatus().then(setStatus).catch(() => setStatus(null)),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  return (
    <div className="flex flex-col gap-4">
      <div role="tablist" className="flex flex-wrap gap-1 border-b pb-2" style={{ borderBottomWidth: 'var(--hairline)' }}>
        {TABS.map((t) => (
          <button
            key={t.id}
            role="tab"
            aria-selected={tab === t.id}
            onClick={() => setTab(t.id)}
            className={cn(
              'rounded-md px-3 font-medium transition-colors',
              tab === t.id
                ? 'bg-accent text-accent-foreground'
                : 'text-muted-foreground hover:bg-secondary hover:text-foreground',
            )}
            style={{ fontSize: 'var(--text-small)', height: 'var(--control-h)' }}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'data' && <DataSection />}

      {/* The two wizard-backed tabs mount only once the status is in hand, so their fields can be
          initialised from it directly instead of being written into by an effect one render
          later -- which is the version that flickers and, worse, clobbers whatever was typed in
          between. */}
      {tab === 'vrchat' &&
        (status ? <VRChatSection status={status} refresh={refresh} /> : <Placeholder>Loading…</Placeholder>)}
      {tab === 'integrations' &&
        (status ? <IntegrationsSection status={status} refresh={refresh} /> : <Placeholder>Loading…</Placeholder>)}

      {tab === 'evidence' && <EvidenceSection />}

      {tab === 'sync' && <SyncSection />}
    </div>
  )
}
