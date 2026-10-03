import { useCallback, useEffect, useState } from 'react'
import { Outcome, Placeholder, Switch } from '@/components/settings/fields'
import { SettingsCard, SettingsSection } from '@/components/settings/SettingsCard'
import { ApiError } from '@/lib/api'
import { postsApi, type PostSettings } from '@/lib/posts'

/**
 * Settings, Posts (posts design §4.6): Pause all posting, and whether posts go to Discord, to the
 * VRChat group and to Bluesky. Bluesky posts are the Bluesky topic's Posting switch, shown here too,
 * and go on only once that account passed Check. Each switch saves at once: one yes or no with
 * nothing to save beside it.
 */
export function PostsSection() {
  const [settings, setSettings] = useState<PostSettings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      postsApi
        .settings()
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

  const change = (next: Partial<PostSettings>) => {
    setBusy(true)
    setError(null)

    postsApi
      .setSettings(next)
      .then(setSettings)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  if (!settings) return <Placeholder tone={loadError ? 'danger' : 'loading'} onTryAgain={load}>{loadError}</Placeholder>

  return (
    <SettingsSection id="posts" title="Posts">
      <SettingsCard title="Posting" footer={<Outcome tone="problem">{error}</Outcome>}>
        <div className="flex flex-col gap-1">
          <Switch checked={settings.paused} disabled={busy} onChange={(paused) => change({ paused })}>
            Pause all posting
          </Switch>
          <Switch checked={settings.discord} disabled={busy} onChange={(discord) => change({ discord })}>
            Discord posts
          </Switch>
          <Switch checked={settings.vrChat} disabled={busy} onChange={(vrChat) => change({ vrChat })}>
            VRChat posts
          </Switch>
          <Switch
            checked={!!settings.bluesky}
            disabled={busy || (!settings.bluesky && !settings.blueskyCanPost)}
            onChange={(bluesky) => change({ bluesky })}
          >
            Bluesky posts
          </Switch>
        </div>
      </SettingsCard>
    </SettingsSection>
  )
}
