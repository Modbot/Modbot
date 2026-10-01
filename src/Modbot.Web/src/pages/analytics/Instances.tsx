import { useCallback } from 'react'
import { InstanceCards } from '@/components/InstanceCards'
import { InstanceTable } from '@/components/InstanceTable'
import { api, type CurrentUser } from '@/lib/api'
import type { PageId } from '@/lib/nav'
import { GroupHeaderFor } from './GroupHeader'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { PageMessage, Panel } from './shared'
import { useAnalytics } from './useAnalytics'

/**
 * The Instances tab of the VRChat page, as vrchat.com shows a group's instances: what is open now,
 * then what ran lately, which is Modbot's own. The charts about when the community is active are on
 * the Stats page's Activity tab (Stats page design).
 *
 * It opens under the VRChat page's header with Instances marked, and the sidebar lights VRChat. The
 * header is read once from what Modbot stored; the page does not wait for it. The recent list is the
 * last thirty days, the range the tab always opened on.
 */
export function Instances({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const load = useCallback((q: string) => api.instancesAnalytics(q), [])
  const { data, error, reload } = useAnalytics(load, 30)

  const header = <GroupHeaderFor me={me} pathOf={pathOf} active="analytics-instances" />

  return (
    <div className="flex flex-col gap-3">
      {header}

      {error && <PageMessage tone="danger" onTryAgain={reload}>{error}</PageMessage>}

      {!data && !error && <PageMessage tone="loading" />}

      {data && (
        <PanelGrid className="grid-cols-1">
          {data.openNow.length > 0 && (
            <Panel title="Open right now" flush>
              <InstanceCards instances={data.openNow} />
            </Panel>
          )}

          <Panel title="Recent instances" flush>
            {data.recent.length === 0 ? <EmptyRow>No instances yet.</EmptyRow> : <InstanceTable instances={data.recent} />}
          </Panel>
        </PanelGrid>
      )}
    </div>
  )
}
