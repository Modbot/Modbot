import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, type PublicRoomsView } from '@/lib/api'
import { Fact, Outcome, Placeholder, Switch } from './fields'
import { SettingsCard } from './SettingsCard'

/**
 * Modbot Cloud: whether this group's rooms that anyone can join are listed on modbot.co.
 *
 * Saves on the switch rather than on a Save button, because it is one switch and there is nothing
 * else on the card to save with it. Rooms limited to members, or to members and their friends, are
 * never sent whatever this says — see PublicRoomsReportBuilder.
 */
export function PublicRoomsCard() {
  const [view, setView] = useState<PublicRoomsView | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .publicRooms()
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
      .setPublicRooms(shared)
      .then(setView)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard title="Modbot Cloud">
      {!view ? (
        <Placeholder>{error ?? 'Loading…'}</Placeholder>
      ) : (
        <>
          <Switch checked={view.shared} disabled={saving || view.cloudDisabled} onChange={choose}>
            List this group&rsquo;s public rooms on modbot.co
          </Switch>
          {view.cloudDisabled && <Fact label="Modbot Cloud" value="Off (MODBOT_CLOUD_DISABLED)" />}
          <Fact
            label="Last sent"
            value={view.lastSentAt ? new Date(view.lastSentAt).toLocaleString() : '—'}
          />
          <Outcome tone="problem">{error}</Outcome>
        </>
      )}
    </SettingsCard>
  )
}
