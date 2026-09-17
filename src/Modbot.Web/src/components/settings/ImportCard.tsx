import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, type ImportView } from '@/lib/api'
import { Field, Outcome, Placeholder } from './fields'
import { SettingsCard } from './SettingsCard'
import { failure, when } from './api/shared'

/**
 * Settings → Host & Database → Import (import design §9).
 *
 * A file, a source label, and two buttons: one that reads and counts, one that writes. The list
 * under them is every past import with its counts; it asks the server again every two seconds
 * while one is still queued or running, which is how progress shows.
 */
export function ImportCard() {
  const [imports, setImports] = useState<ImportView[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [file, setFile] = useState<File | null>(null)
  const [source, setSource] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [opened, setOpened] = useState<string | null>(null)
  const picker = useRef<HTMLInputElement>(null)

  const load = useCallback(
    () =>
      api
        .imports()
        .then((d) => {
          setImports(d.imports)
          setError(null)
        })
        .catch((e: unknown) => setError(failure(e, 'Could not load imports.'))),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const running = imports?.some((i) => i.status === 'Queued' || i.status === 'Running') ?? false

  useEffect(() => {
    if (!running) return
    const timer = window.setInterval(() => void load(), 2000)
    return () => window.clearInterval(timer)
  }, [running, load])

  const start = (dryRun: boolean) => {
    if (!file) return
    setBusy(true)
    setProblem(null)
    api
      .startImport(file, source.trim(), dryRun)
      .then(() => {
        setFile(null)
        if (picker.current) picker.current.value = ''
        return load()
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not start the import.')))
      .finally(() => setBusy(false))
  }

  const ready = !!file && source.trim().length > 0 && !busy

  return (
    <SettingsCard
      span={12}
      title="Import"
      footer={
        <>
          <Button size="sm" variant="outline" disabled={!ready} onClick={() => start(true)}>
            Dry run
          </Button>
          <Button size="sm" disabled={!ready} onClick={() => start(false)}>
            {busy ? 'Uploading…' : 'Import'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="grid max-w-lg gap-3 sm:grid-cols-2">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">File</span>
          <Input
            ref={picker}
            type="file"
            accept=".json,.ndjson,.jsonl,application/json,application/x-ndjson"
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          />
        </label>
        <Field label="Source" placeholder="old-bot" value={source} onChange={setSource} />
      </div>

      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !imports ? (
        <Placeholder>Loading…</Placeholder>
      ) : imports.length === 0 ? null : (
        <div className="overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="text-left text-muted-foreground">
              <tr>
                <th className="py-1 pr-3 font-normal">When</th>
                <th className="py-1 pr-3 font-normal">Source</th>
                <th className="py-1 pr-3 font-normal">File</th>
                <th className="py-1 pr-3 font-normal">By</th>
                <th className="py-1 pr-3 font-normal">Status</th>
                <th className="py-1 pr-3 text-right font-normal">Received</th>
                <th className="py-1 pr-3 text-right font-normal">Imported</th>
                <th className="py-1 pr-3 text-right font-normal">Skipped</th>
                <th className="py-1 pr-3 text-right font-normal">Rejected</th>
                <th />
              </tr>
            </thead>
            <tbody className="divide-y">
              {imports.map((i) => (
                <ImportRow
                  key={i.id}
                  item={i}
                  open={opened === i.id}
                  onToggle={() => setOpened(opened === i.id ? null : i.id)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </SettingsCard>
  )
}

function ImportRow({
  item,
  open,
  onToggle,
}: {
  item: ImportView
  open: boolean
  onToggle: () => void
}) {
  const details = item.rejections.length > 0 || item.error
  const finished = item.status === 'Done' || item.status === 'Failed'

  return (
    <>
      <tr className={finished ? '' : 'text-muted-foreground'}>
        <td className="py-2 pr-3">{when(item.createdAt)}</td>
        <td className="py-2 pr-3 font-medium">{item.source}</td>
        <td className="py-2 pr-3">{item.fileName ?? '—'}</td>
        <td className="py-2 pr-3">{item.startedBy}</td>
        <td className="py-2 pr-3">
          <span className="inline-flex items-center gap-1.5">
            <Badge variant={item.status === 'Failed' ? 'destructive' : item.status === 'Done' ? 'secondary' : 'outline'}>
              {item.status}
            </Badge>
            {item.dryRun && <Badge variant="outline">Dry run</Badge>}
          </span>
        </td>
        <td className="py-2 pr-3 text-right tabular-nums">{item.received.toLocaleString()}</td>
        <td className="py-2 pr-3 text-right tabular-nums">{item.imported.toLocaleString()}</td>
        <td className="py-2 pr-3 text-right tabular-nums">{item.skipped.toLocaleString()}</td>
        <td className="py-2 pr-3 text-right tabular-nums">{item.rejected.toLocaleString()}</td>
        <td className="py-2 text-right">
          {details && (
            <Button size="xs" variant="ghost" onClick={onToggle}>
              {open ? 'Hide' : 'Show'}
            </Button>
          )}
        </td>
      </tr>
      {open && details && (
        <tr>
          <td colSpan={10} className="py-2">
            {item.error && <Outcome tone="problem">{item.error}</Outcome>}
            {item.rejections.length > 0 && (
              <ul className="mt-1 flex flex-col gap-0.5 text-muted-foreground">
                {item.rejections.map((r, n) => (
                  <li key={n}>
                    <span className="font-mono">{r.line}</span>: {r.reason}
                  </li>
                ))}
              </ul>
            )}
          </td>
        </tr>
      )}
    </>
  )
}
