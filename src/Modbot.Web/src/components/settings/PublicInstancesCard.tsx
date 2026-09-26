import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, type PublicInstancesView } from '@/lib/api'
import { EmptyRow } from '@/components/PanelGrid'
import { Row } from '@/components/ui/fact-row'
import { Outcome, Switch } from './fields'
import { SettingsCard } from './SettingsCard'
import { dateTime } from '@/components/charts/format'

/**
 * Modbot Cloud: whether this group's instances that anyone can join are listed on modbot.co.
 *
 * Saves on the switch rather than on a Save button, because it is one switch and there is nothing
 * else on the card to save with it. Instances limited to members, or to members and their friends, are
 * never sent whatever this says — see PublicInstancesReportBuilder.
 */
/** `span` is 12 when the card has no partner beside it on the tab. */
export function PublicInstancesCard({ span = 6 }: { span?: 6 | 12 }) {
  const [view, setView] = useState<PublicInstancesView | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .publicInstances()
        .then((next) => {
          setView(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load the setting.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const choose = (shared: boolean) => {
    setSaving(true)
    setError(null)
    api
      .setPublicInstances(shared)
      .then(setView)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard title="Modbot Cloud" span={span}>
      {!view ? (
        <EmptyRow className="px-0" tone={error ? 'danger' : undefined}>{error ?? 'Loading…'}</EmptyRow>
      ) : (
        <>
          <Switch checked={view.shared} disabled={saving || view.cloudDisabled} onChange={choose}>
            List this group&rsquo;s public instances on modbot.co
          </Switch>
          <div className="max-w-lg">
            {view.cloudDisabled && <Row label="Modbot Cloud" value="Off (MODBOT_CLOUD_DISABLED)" />}
            <Row
              label="Last sent"
              value={view.lastSentAt ? dateTime(view.lastSentAt) : '—'}
              mono={!!view.lastSentAt}
            />
          </div>
          <Outcome tone="problem">{error}</Outcome>
        </>
      )}
    </SettingsCard>
  )
}
