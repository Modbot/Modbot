import { useCallback, useEffect, useState } from 'react'
import { Outcome, Placeholder, Switch } from '@/components/settings/fields'
import { SettingsCard } from '@/components/settings/SettingsCard'
import { Select } from '@/components/ui/select'
import { ApiError } from '@/lib/api'
import { postsApi, type LivePostScope, type LivePostSettings } from '@/lib/posts'

const SCOPES: { value: LivePostScope; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'calendar', label: 'Calendar events' },
  { value: 'any', label: 'Any group instance' },
  { value: 'modbot', label: 'Opened from Modbot' },
]

/**
 * Settings, Posts, Live posts: which group instances get a "we're live" group post, and whether it
 * is made as a draft in Marketing for a host to approve or posted at once. Each choice saves at
 * once, as the Posting card's switches do.
 */
export function LivePostsCard() {
  const [settings, setSettings] = useState<LivePostSettings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      postsApi
        .livePostSettings()
        .then((next) => {
          setSettings(next)
          setLoadError(null)
        })
        .catch((e: unknown) => setLoadError(e instanceof ApiError ? e.message : 'Could not load.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const change = (next: Partial<LivePostSettings>) => {
    setBusy(true)
    setError(null)

    postsApi
      .setLivePostSettings(next)
      .then(setSettings)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  if (!settings) {
    return (
      <Placeholder tone={loadError ? 'danger' : 'loading'} onTryAgain={load}>
        {loadError}
      </Placeholder>
    )
  }

  return (
    <SettingsCard title="Live posts" footer={<Outcome tone="problem">{error}</Outcome>}>
      <div className="flex flex-col gap-3">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Instances</span>
          <Select
            value={settings.scope}
            disabled={busy}
            onChange={(scope) => change({ scope: scope as LivePostScope })}
            aria-label="Instances that get a live post"
          >
            {SCOPES.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </Select>
        </label>
        <Switch checked={settings.auto} disabled={busy} onChange={(auto) => change({ auto })}>
          Post without a draft
        </Switch>
      </div>
    </SettingsCard>
  )
}
