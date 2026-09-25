import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type HealthAlertView } from '@/lib/api'
import { EmptyRow } from '@/components/PanelGrid'
import { Checkbox, Field, Outcome } from './fields'
import { SettingsCard } from './SettingsCard'

/**
 * Health alerts: what Modbot emails somebody about when it stops working.
 *
 * Beside the SMTP card because it is the same mail, and because "who gets this" is only a real
 * question once there is a way to send it. Everything is off until somebody turns it on.
 */
export function HealthAlertsCard() {
  const [view, setView] = useState<HealthAlertView | null>(null)
  const [checks, setChecks] = useState<string[]>([])
  const [recipients, setRecipients] = useState<string[]>([])
  const [quietHours, setQuietHours] = useState('')
  const [storageGb, setStorageGb] = useState('')
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .healthAlerts()
        .then((next) => {
          setView(next)
          setChecks(next.watches.filter((w) => w.on).map((w) => w.check))
          setRecipients(next.recipients.filter((r) => r.chosen).map((r) => r.userId))
          setQuietHours(String(next.quietHours))
          setStorageGb(String(next.storageWarnGb))
          setProblem(null)
        })
        .catch((e: unknown) =>
          setProblem(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to read the health alerts.'
              : 'Could not load the health alerts.',
          ),
        ),
    [],
  )

  useEffect(() => void load(), [load])

  const save = () => {
    setSaving(true)
    setSaved(false)
    setProblem(null)

    api
      .setHealthAlerts({
        quietHours: Number(quietHours) || 0,
        storageWarnGb: Number(storageGb) || 0,
        checksOn: checks,
        recipientUserIds: recipients,
      })
      .then(() => {
        setSaved(true)
        return load()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  const toggle = (list: string[], set: (next: string[]) => void, id: string, on: boolean) =>
    set(on ? [...list.filter((v) => v !== id), id] : list.filter((v) => v !== id))

  return (
    <SettingsCard
      title="Health alerts"
      span={12}
      footer={
        <>
          <Button size="xs" disabled={!view || saving} onClick={save}>
            {saving ? 'Saving…' : 'Save alerts'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {!view ? (
        <EmptyRow className="px-0">Loading…</EmptyRow>
      ) : (
        <div className="grid gap-6 lg:grid-cols-2">
          <div className="flex flex-col gap-2">
            <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
              What is watched
            </div>
            {view.watches.map((watch) => (
              <div key={watch.check} className="flex items-baseline gap-2">
                <Checkbox
                  checked={checks.includes(watch.check)}
                  onChange={(on) => toggle(checks, setChecks, watch.check, on)}
                >
                  {watch.label}
                </Checkbox>
                {watch.problem && (
                  <span className="text-warn" style={{ fontSize: 'var(--text-small)' }}>
                    {watch.detail ?? 'Needs looking at'}
                  </span>
                )}
              </div>
            ))}
          </div>

          <div className="flex flex-col gap-4">
            <div className="flex flex-col gap-2">
              <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
                Who is emailed
              </div>
              {view.recipients.length === 0 ? (
                <EmptyRow className="px-0">No staff accounts.</EmptyRow>
              ) : (
                view.recipients.map((person) => (
                  <Checkbox
                    key={person.userId}
                    checked={recipients.includes(person.userId)}
                    disabled={!person.email}
                    onChange={(on) => toggle(recipients, setRecipients, person.userId, on)}
                  >
                    {person.email ? `${person.username} · ${person.email}` : `${person.username} · no address`}
                  </Checkbox>
                ))
              )}
            </div>

            <div className="grid max-w-sm grid-cols-2 items-end gap-3">
              <Field label="Quiet time (hours)" placeholder="6" value={quietHours} onChange={setQuietHours} />
              <Field
                label="Database bigger than (GB)"
                placeholder="0"
                value={storageGb}
                onChange={setStorageGb}
              />
            </div>
          </div>
        </div>
      )}
    </SettingsCard>
  )
}
