import { useCallback, useEffect, useState } from 'react'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Select } from '@/components/ui/select'
import { api, ApiError, type SpotlightView } from '@/lib/api'

/**
 * A weekly spotlight draft made in staff mode has no member yet (spotlight design §3). This card
 * lists the members who opted in; picking one fills the draft with their card, and the note is
 * written and the post sent from the list below as for any post. Drawn only while a draft waits.
 */
export function SpotlightChooser({ version, onPicked }: { version: number; onPicked: () => void }) {
  const [view, setView] = useState<SpotlightView | null>(null)
  const [member, setMember] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .spotlight()
        .then(setView)
        // A page that cannot read the spotlight simply has no card.
        .catch(() => setView(null)),
    [],
  )

  useEffect(() => {
    void load()
  }, [load, version])

  if (!view?.pending) return null

  const pending = view.pending

  const choose = () => {
    setBusy(true)
    setProblem(null)

    api
      .pickSpotlightMember(pending.id, member)
      .then(() => {
        setMember('')
        onPicked()
        return load()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not choose.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card className="flex flex-wrap items-end gap-3 p-(--panel-pad)">
      <label className="flex min-w-48 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Spotlight member</span>
        <Select value={member} onChange={setMember} aria-label="Spotlight member">
          <option value="">Pick a member</option>
          {view.members.map((m) => (
            <option key={m.id} value={m.id}>
              {m.name}
            </option>
          ))}
        </Select>
      </label>
      <Button type="button" disabled={busy || !member} onClick={choose}>
        {busy ? 'Choosing…' : 'Choose'}
      </Button>
      <Outcome tone="problem">{problem}</Outcome>
    </Card>
  )
}
