import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, type PublicInstancesView } from '@/lib/api'
import { EmptyRow } from '@/components/PanelGrid'
import { Fact, Outcome, Switch } from './fields'
import { SettingsCard } from './SettingsCard'

/**
 * Modbot Cloud: whether this group's instances that anyone can join are listed on modbot.co.
 *
 * Saves on the switch rather than on a Save button, because it is one switch and there is nothing
 * else on the card to save with it. Instances limited to members, or to members and their friends, are
 * never sent whatever this says — see PublicInstancesReportBuilder.
 */
export function PublicInstancesCard() {
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
    <SettingsCard title="Modbot Cloud">
      {!view ? (
        <EmptyRow className="px-0">{error ?? 'Loading…'}</EmptyRow>
      ) : (
        <>
          <Switch checked={view.shared} disabled={saving || view.cloudDisabled} onChange={choose}>
            List this group&rsquo;s public instances on modbot.co
          </Switch>
          {view.cloudDisabled && <Fact label="Modbot Cloud" value="Off (MODBOT_CLOUD_DISABLED)" />}
          <Fact
            label="Last sent"
            value={view.lastSentAt ? new Date(view.lastSentAt).toLocaleString() : '—'}
            mono={!!view.lastSentAt}
          />
          <Outcome tone="problem">{error}</Outcome>
        </>
      )}
    </SettingsCard>
  )
}
