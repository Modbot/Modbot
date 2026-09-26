import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { EmptyRow } from '@/components/PanelGrid'
import { failure, moderationApi, type HubListView } from '@/lib/autoMod'
import { Outcome } from '../fields'

/** The lists Modbot Hub offers, each with an Add button. Read again every time it opens. */
export function HubListsDialog({
  open,
  onClose,
  onAdded,
}: {
  open: boolean
  onClose: () => void
  onAdded: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      {open && <HubLists onAdded={onAdded} />}
    </Dialog>
  )
}

function HubLists({ onAdded }: { onAdded: () => void }) {
  const [lists, setLists] = useState<HubListView[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [adding, setAdding] = useState<string | null>(null)

  useEffect(() => {
    moderationApi
      .hub()
      .then((r) => {
        setLists(r.lists)
        setProblem(r.error)
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not reach Modbot Hub.')))
  }, [])

  const add = (id: string) => {
    setAdding(id)
    setProblem(null)

    moderationApi
      .addHubList(id)
      .then(() => {
        setLists((all) => all?.map((l) => (l.id === id ? { ...l, subscribed: true } : l)) ?? null)
        onAdded()
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not add the list.')))
      .finally(() => setAdding(null))
  }

  return (
    <DialogContent
      title="Modbot Hub term lists"
      className="max-w-[720px]"
      bodyClassName="flex max-h-[75vh] flex-col gap-3 overflow-y-auto"
    >
      {lists ? (
        <Outcome tone="problem">{problem}</Outcome>
      ) : (
        <EmptyRow className="px-0" tone={problem ? 'danger' : undefined}>
          {problem ?? 'Loading…'}
        </EmptyRow>
      )}
      <ul className="flex flex-col">
        {lists?.map((list) => (
          <li
            key={list.id}
            className="flex items-start gap-3 border-b border-b-(length:--hairline) py-2 last:border-0"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <div className="min-w-0 flex-1">
              <div className="font-medium">{list.name}</div>
              <div className="text-muted-foreground">
                {[`${list.ruleCount} rules`, list.version, list.suitableFor.join(', ')]
                  .filter(Boolean)
                  .join(' · ')}
              </div>
            </div>
            <Button
              size="xs"
              variant="outline"
              disabled={list.subscribed || adding !== null}
              onClick={() => add(list.id)}
            >
              {list.subscribed ? 'Added' : adding === list.id ? 'Adding…' : 'Add'}
            </Button>
          </li>
        ))}
      </ul>
    </DialogContent>
  )
}
