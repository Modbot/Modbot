import { useCallback, useState } from 'react'
import { AppealRow, LogAppealForm } from '@/components/AppealParts'
import { Button } from '@/components/ui/button'
import { Empty, Panel } from '@/components/subject/shared'
import { appealApi, type AppealList } from '@/lib/appeals'
import { useLoad } from '@/lib/useLoad'

/**
 * This person's appeals, under their case files on the Cases tab: when each came in, how it ended,
 * and a button to write a new one down for somebody who may.
 */
export function PersonAppeals({ vrchatId }: { vrchatId: string }) {
  const [version, setVersion] = useState(0)
  const [logging, setLogging] = useState(false)

  const load = useCallback(() => appealApi.list({ userId: vrchatId, limit: 50 }), [vrchatId])
  const { data, error, reload } = useLoad<AppealList>(load, version)

  const again = () => setVersion((n) => n + 1)

  if (error) {
    return (
      <Panel title="Appeals" flush>
        <Empty tone="danger" onTryAgain={reload}>{error}</Empty>
      </Panel>
    )
  }
  if (!data) return null

  // Nobody has appealed and this person cannot write one down: there is nothing to show.
  if (data.appeals.length === 0 && !data.canLog) return null

  return (
    <Panel
      title="Appeals"
      flush
      right={
        data.canLog && !logging ? (
          <Button size="xs" variant="outline" onClick={() => setLogging(true)}>
            Log appeal
          </Button>
        ) : undefined
      }
    >
      {logging && (
        <LogAppealForm
          userId={vrchatId}
          now={data.now}
          onCancel={() => setLogging(false)}
          onLogged={() => {
            setLogging(false)
            again()
          }}
        />
      )}
      {data.appeals.length === 0 ? (
        !logging && <Empty>No appeals.</Empty>
      ) : (
        <ul className="flex flex-col">
          {data.appeals.map((appeal) => (
            <AppealRow key={appeal.id} appeal={appeal} showPerson={false} onChanged={again} />
          ))}
        </ul>
      )}
    </Panel>
  )
}
