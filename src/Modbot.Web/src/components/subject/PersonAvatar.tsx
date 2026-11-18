import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Empty, Note as Muted, Panel } from '@/components/subject/shared'
import { api, ApiError } from '@/lib/api'
import { useLoad } from '@/lib/useLoad'

/**
 * The avatar a person was last seen wearing, with a button that lists it as a problem avatar.
 * The caller shows it only to somebody who may manage settings, the permission the list itself needs.
 */
export function PersonAvatar({ vrchatId }: { vrchatId: string }) {
  const [version, setVersion] = useState(0)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const load = useCallback(() => api.personAvatar(vrchatId), [vrchatId])
  const { data, error, reload } = useLoad(load, version)

  if (error) return <Empty tone="danger" onTryAgain={reload}>{error}</Empty>
  if (!data || !data.avatarName) return null

  const mark = () => {
    setBusy(true)
    setProblem(null)

    api
      .markPersonAvatar(vrchatId)
      .then(() => setVersion((n) => n + 1))
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save that.'))
      .finally(() => setBusy(false))
  }

  return (
    <Panel
      title="Avatar"
      right={
        data.listed ? (
          <Badge variant="destructive">Problem avatar</Badge>
        ) : (
          <Button size="xs" variant="outline" disabled={busy} onClick={mark}>
            Mark avatar
          </Button>
        )
      }
    >
      <span className="break-words" style={{ fontSize: 'var(--text-small)' }}>
        {data.avatarName}
      </span>
      {problem && <Muted className="text-destructive">{problem}</Muted>}
    </Panel>
  )
}
