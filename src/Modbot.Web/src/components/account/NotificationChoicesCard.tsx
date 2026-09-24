import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Select } from '@/components/ui/select'
import { ApiError, api, type NotificationChoice, type NotificationLevel } from '@/lib/api'

const LEVELS: { value: NotificationLevel; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'critical', label: 'Critical only' },
  { value: 'warning', label: 'Critical and warnings' },
  { value: 'everything', label: 'Everything' },
]

/**
 * Where this person's own notifications go (foundation §4.5.1).
 *
 * On the account page rather than in Settings because it is one person's choice about their own
 * alerts, the same kind of thing as their own password — nothing here needs a permission and
 * nothing here changes what anybody else gets.
 */
export function NotificationChoicesCard() {
  const [channels, setChannels] = useState<NotificationChoice[] | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true

    api
      .notificationChoices()
      .then((r) => live && setChannels(r.channels))
      .catch((e) => live && setError(e instanceof ApiError ? e.message : 'Could not read your notification settings.'))

    return () => {
      live = false
    }
  }, [])

  const set = (channel: string, patch: Partial<NotificationChoice>) =>
    setChannels((all) => all?.map((c) => (c.channel === channel ? { ...c, ...patch } : c)) ?? null)

  const save = async () => {
    if (!channels) return

    setSaving(true)
    setError(null)

    try {
      const saved = await api.setNotificationChoices(
        channels.map((c) => ({ channel: c.channel, level: c.level, dailySummary: c.dailySummary })),
      )
      setChannels(saved.channels)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not save your notification settings.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Notifications</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-1 flex-col gap-3">
        {error && (
          <div className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {error}
          </div>
        )}

        {channels?.map((c) => (
          <div key={c.channel} className="grid gap-2 sm:grid-cols-2">
            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">{c.label}</span>
              <Select
                value={c.level}
                onChange={(v) => set(c.channel, { level: v as NotificationLevel })}
                aria-label={c.label}
              >
                {LEVELS.map((l) => (
                  <option key={l.value} value={l.value}>
                    {l.label}
                  </option>
                ))}
              </Select>
              {!c.canReach && <span className="text-muted-foreground">Cannot reach you</span>}
            </label>

            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">{`${c.label} daily summary`}</span>
              <Select
                value={c.dailySummary ? 'on' : 'off'}
                onChange={(v) => set(c.channel, { dailySummary: v === 'on' })}
                aria-label={`${c.label} daily summary`}
              >
                <option value="on">On</option>
                <option value="off">Off</option>
              </Select>
            </label>
          </div>
        ))}
      </CardContent>
      <CardFooter>
        <Button size="sm" onClick={save} disabled={saving || !channels}>
          {saving ? 'Saving…' : 'Save'}
        </Button>
      </CardFooter>
    </Card>
  )
}
