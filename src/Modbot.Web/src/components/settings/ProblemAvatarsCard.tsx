import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, type ProblemAvatar } from '@/lib/api'
import { Outcome } from './fields'
import { failure } from './api/shared'
import { SettingsCard } from './SettingsCard'

/**
 * Settings → Moderation: the avatar names that raise a flagged alert on whoever wears one.
 *
 * Names are added from a person's popup (Mark avatar); this is where they are read and taken off.
 * The server matches by name, because VRChat's log never gives an avatar id.
 */
export function ProblemAvatarsCard() {
  const [avatars, setAvatars] = useState<ProblemAvatar[] | null>(null)
  const [busyId, setBusyId] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    api
      .problemAvatars()
      .then(setAvatars)
      .catch((e: unknown) => setError(failure(e, 'Could not load the avatars.')))
  }, [])

  const remove = (id: string) => {
    setBusyId(id)
    setError('')

    api
      .removeProblemAvatar(id)
      .then(() => setAvatars((current) => current?.filter((a) => a.id !== id) ?? null))
      .catch((e: unknown) => setError(failure(e, 'Could not remove it.')))
      .finally(() => setBusyId(''))
  }

  return (
    <SettingsCard title="Problem avatars" span={12} footer={error ? <Outcome tone="problem">{error}</Outcome> : undefined}>
      {avatars?.length === 0 && <p className="text-muted-foreground">None</p>}
      {avatars && avatars.length > 0 && (
        <ul className="flex flex-col gap-1">
          {avatars.map((a) => (
            <li key={a.id} className="flex items-center justify-between gap-3">
              <span className="min-w-0 break-words">{a.name}</span>
              <Button variant="outline" size="xs" disabled={busyId === a.id} onClick={() => remove(a.id)}>
                Remove
              </Button>
            </li>
          ))}
        </ul>
      )}
    </SettingsCard>
  )
}
