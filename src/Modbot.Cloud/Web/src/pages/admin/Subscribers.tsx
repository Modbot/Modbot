import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { useAdminLoad } from '@/lib/useLoad'

const PAGE = 50

/** Everybody who asked to hear from Modbot, and where they ticked the box. */
export function Subscribers() {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [offset, setOffset] = useState(0)

  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(typed.trim())
      setOffset(0)
    }, 300)
    return () => clearTimeout(timer)
  }, [typed])

  const counts = useAdminLoad(() => api.subscriberCounts(), [])
  const { data, error } = useAdminLoad(() => api.subscribers(search, offset, PAGE), [search, offset])

  return (
    <>
      <h1 className="font-display text-lg">Subscribers</h1>

      <div className="grid gap-4 sm:grid-cols-3">
        <Count label="On the list" value={counts.data?.subscribed} />
        <Count label="Left" value={counts.data?.unsubscribed} />
        <Count label="Everyone ever" value={counts.data?.total} />
      </div>

      {counts.data && Object.keys(counts.data.bySource).length > 0 && (
        <Card className="gap-0 py-0">
          <h2 className="font-display border-b px-4 py-3 text-base">Where from</h2>
          <ul className="flex flex-col">
            {Object.entries(counts.data.bySource)
              .sort((a, b) => b[1] - a[1])
              .map(([source, count]) => (
                <li key={source} className="flex items-center justify-between border-t px-4 py-2 first:border-t-0">
                  <span className="font-mono">{source}</span>
                  <span className="text-muted-foreground">{count.toLocaleString()}</span>
                </li>
              ))}
          </ul>
        </Card>
      )}

      <Input
        value={typed}
        onChange={(e) => setTyped(e.target.value)}
        placeholder="Search"
        aria-label="Search subscribers"
      />

      <Card className="gap-0 py-0">
        {error && error.status !== 401 ? (
          <p className="px-4 py-6 text-destructive">{error.message}</p>
        ) : !data ? (
          <p className="px-4 py-6 text-muted-foreground">Loading</p>
        ) : data.items.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No subscribers</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="px-4">Address</TableHead>
                <TableHead className="px-4">Where from</TableHead>
                <TableHead className="px-4">First seen</TableHead>
                <TableHead className="px-4">Last seen</TableHead>
                <TableHead className="px-4">Left</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((subscriber) => (
                <TableRow key={subscriber.email}>
                  <TableCell className="px-4 font-mono">{subscriber.email}</TableCell>
                  <TableCell className="px-4 font-mono">{subscriber.source ?? '—'}</TableCell>
                  <TableCell className="px-4" title={when(subscriber.firstSeenAt)}>
                    {ago(subscriber.firstSeenAt)}
                  </TableCell>
                  <TableCell className="px-4" title={when(subscriber.lastSeenAt)}>
                    {ago(subscriber.lastSeenAt)}
                  </TableCell>
                  <TableCell className="px-4">
                    {subscriber.unsubscribedAt ? (
                      <Badge variant="secondary" title={when(subscriber.unsubscribedAt)}>
                        Left
                      </Badge>
                    ) : (
                      '—'
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>

      {data && data.total > PAGE && (
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={offset === 0}
            onClick={() => setOffset(Math.max(0, offset - PAGE))}
          >
            Previous
          </Button>
          <span className="text-muted-foreground">
            {offset + 1}–{Math.min(offset + PAGE, data.total)} of {data.total}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={offset + PAGE >= data.total}
            onClick={() => setOffset(offset + PAGE)}
          >
            Next
          </Button>
        </div>
      )}
    </>
  )
}

function Count({ label, value }: { label: string; value: number | undefined }) {
  return (
    <Card className="gap-1 px-4 py-4">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-display text-lg">{value === undefined ? '—' : value.toLocaleString()}</span>
    </Card>
  )
}
