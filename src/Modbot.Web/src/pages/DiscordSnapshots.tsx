import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Select } from '@/components/ui/select'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { ConfirmButton, Outcome } from '@/components/settings/fields'
import { Empty } from '@/components/ListParts'
import {
  api,
  ApiError,
  type CurrentUser,
  type DiscordSnapshotChange,
  type DiscordSnapshotCompareResponse,
  type DiscordSnapshotDetail,
  type DiscordSnapshotRestorePreview,
  type DiscordSnapshotsResponse,
} from '@/lib/api'
import { dateTime } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { isFinal } from '@/lib/tryAgain'
import { ServerHeader } from '@/pages/analytics/ServerHeader'
import { Panel } from '@/pages/analytics/shared'
import { useServerProfile } from '@/pages/analytics/useServerProfile'

const SCHEDULES: Record<DiscordSnapshotsResponse['schedule'], string> = {
  off: 'Off',
  weekly: 'Weekly',
  'every-two-weeks': 'Every 2 weeks',
  monthly: 'Monthly',
}

const CHANGES: Record<DiscordSnapshotChange['what'], string> = {
  added: 'Added',
  removed: 'Removed',
  changed: 'Changed',
}

type Snapshots = DiscordSnapshotsResponse['snapshots']

/**
 * The Discord page's Snapshots tab: the server's roles, channels and permissions saved on a
 * schedule or by hand, what changed between two of them, and one role's or channel's permissions
 * put back.
 *
 * Seeing the page needs See analytics. Taking, deleting and restoring need Change settings, so
 * their controls are only drawn for somebody who has it; the server checks each anyway. A restore
 * shows its exact change first and the press that runs it carries that change's fingerprint.
 */
export function DiscordSnapshots({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const server = useServerProfile(me)

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-snapshots" />}
      <SnapshotsReport canChange={can(me, 'ManageSettings')} />
    </div>
  )
}

function SnapshotsReport({ canChange }: { canChange: boolean }) {
  const [data, setData] = useState<DiscordSnapshotsResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorFinal, setErrorFinal] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .discordSnapshots()
      .then((next) => {
        if (cancelled) return
        setData(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setErrorFinal(isFinal(e))
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view analytics.'
            : 'Could not load the Discord snapshots.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [tries])

  const reload = useCallback(() => setTries((n) => n + 1), [])

  if (error || !data) {
    return (
      <Empty
        tone={error ? 'danger' : 'loading'}
        onTryAgain={
          errorFinal
            ? null
            : () => {
                setError(null)
                reload()
              }
        }
      >
        {error}
      </Empty>
    )
  }

  return (
    <>
      <SnapshotList data={data} canChange={canChange} onChanged={reload} />
      {data.snapshots.length > 0 && <Compare snapshots={data.snapshots} />}
      {canChange && data.snapshots.length > 0 && <Restore data={data} />}
    </>
  )
}

function SnapshotList({
  data,
  canChange,
  onChanged,
}: {
  data: DiscordSnapshotsResponse
  canChange: boolean
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const run = (action: () => Promise<unknown>, failed: string) => {
    setBusy(true)
    setProblem(null)

    action()
      .then(onChanged)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : failed))
      .finally(() => setBusy(false))
  }

  const status = data.nextDue
    ? `${SCHEDULES[data.schedule]} · next ${dateTime(data.nextDue)}`
    : SCHEDULES[data.schedule]

  return (
    <Panel
      title="Snapshots"
      flush
      right={
        <div className="flex items-center gap-3">
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {status}
          </span>
          {canChange && (
            <Button
              type="button"
              size="xs"
              disabled={busy}
              onClick={() => run(() => api.takeDiscordSnapshot(), 'Could not take the snapshot.')}
            >
              {busy ? 'Taking…' : 'Take snapshot'}
            </Button>
          )}
        </div>
      }
    >
      {problem && <Notice tone="warn">{problem}</Notice>}
      {data.snapshots.length === 0 ? (
        <EmptyRow>No snapshots yet.</EmptyRow>
      ) : (
        <Table
          pinFirst
          head={
            <>
              <Th>Taken</Th>
              <Th>By</Th>
              <Th className="text-right">Roles</Th>
              <Th className="text-right">Channels</Th>
              <Th />
            </>
          }
        >
          {data.snapshots.map((s) => (
            <Tr key={s.id}>
              <Td>{dateTime(s.takenAt)}</Td>
              <Td>{s.scheduled ? 'Schedule' : (s.takenBy ?? '—')}</Td>
              <Td className="text-right font-mono">{s.roleCount.toLocaleString()}</Td>
              <Td className="text-right font-mono">{s.channelCount.toLocaleString()}</Td>
              <Td>
                <div className="flex flex-wrap items-center justify-end gap-2">
                  <Button size="xs" variant="outline" asChild>
                    <a href={api.discordSnapshotDownloadHref(s.id)} download>
                      Download
                    </a>
                  </Button>
                  {canChange && (
                    <ConfirmButton
                      disabled={busy}
                      onConfirm={() => run(() => api.deleteDiscordSnapshot(s.id), 'Could not delete the snapshot.')}
                    >
                      Delete
                    </ConfirmButton>
                  )}
                </div>
              </Td>
            </Tr>
          ))}
        </Table>
      )}
    </Panel>
  )
}

function SnapshotSelect({
  label,
  value,
  onChange,
  snapshots,
  withNow,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  snapshots: Snapshots
  withNow?: boolean
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Select value={value} onChange={onChange} aria-label={label}>
        {withNow && <option value="now">Now</option>}
        {snapshots.map((s) => (
          <option key={s.id} value={s.id}>
            {dateTime(s.takenAt)}
          </option>
        ))}
      </Select>
    </label>
  )
}

/** What changed from one snapshot to another, or to the server as it is now. */
function Compare({ snapshots }: { snapshots: Snapshots }) {
  const [from, setFrom] = useState(snapshots[0].id)
  const [to, setTo] = useState('now')
  const [result, setResult] = useState<DiscordSnapshotCompareResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // A snapshot that was deleted stops being a choice; fall back to the newest.
  const known = (id: string) => snapshots.some((s) => s.id === id)
  const fromId = known(from) ? from : snapshots[0].id
  const toId = to === 'now' || known(to) ? to : 'now'

  const compare = () => {
    setBusy(true)
    setProblem(null)

    api
      .compareDiscordSnapshots(fromId, toId)
      .then(setResult)
      .catch((e: unknown) => {
        setResult(null)
        setProblem(e instanceof ApiError ? e.message : 'Could not compare.')
      })
      .finally(() => setBusy(false))
  }

  return (
    <Panel title="What changed">
      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-end gap-3">
          <SnapshotSelect
            label="From snapshot"
            value={fromId}
            onChange={(v) => {
              setFrom(v)
              setResult(null)
            }}
            snapshots={snapshots}
          />
          <SnapshotSelect
            label="To snapshot"
            value={toId}
            onChange={(v) => {
              setTo(v)
              setResult(null)
            }}
            snapshots={snapshots}
            withNow
          />
          <Button type="button" size="xs" onClick={compare} disabled={busy || fromId === toId}>
            {busy ? 'Comparing…' : 'Compare'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </div>

        {result?.error && <Notice tone="warn">{result.error}</Notice>}

        {result && !result.error && result.changes.length === 0 && <EmptyRow>No changes.</EmptyRow>}

        {result && !result.error && result.changes.length > 0 && (
          <Table
            pinFirst
            head={
              <>
                <Th>Name</Th>
                <Th>Kind</Th>
                <Th>Change</Th>
                <Th>Details</Th>
              </>
            }
          >
            {result.changes.map((c) => (
              <Tr key={`${c.kind}:${c.id}`}>
                <Td>{c.name}</Td>
                <Td>{c.kind === 'role' ? 'Role' : 'Channel'}</Td>
                <Td>{CHANGES[c.what]}</Td>
                <Td>
                  {c.details.length === 0 ? (
                    <span className="text-muted-foreground">—</span>
                  ) : (
                    <ul className="flex flex-col gap-0.5">
                      {c.details.map((d) => (
                        <li key={d}>{d}</li>
                      ))}
                    </ul>
                  )}
                </Td>
              </Tr>
            ))}
          </Table>
        )}
      </div>
    </Panel>
  )
}

/** Put one role's or one channel's permissions back, after showing the exact change. */
function Restore({ data }: { data: DiscordSnapshotsResponse }) {
  const { snapshots } = data
  const [snapshotId, setSnapshotId] = useState(snapshots[0].id)
  const [kind, setKind] = useState<'role' | 'channel'>('role')
  const [targetId, setTargetId] = useState('')
  const [detail, setDetail] = useState<DiscordSnapshotDetail | null>(null)
  const [preview, setPreview] = useState<DiscordSnapshotRestorePreview | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)

  const chosen = snapshots.some((s) => s.id === snapshotId) ? snapshotId : snapshots[0].id

  useEffect(() => {
    let cancelled = false

    api
      .discordSnapshot(chosen)
      .then((next) => {
        if (!cancelled) setDetail(next)
      })
      .catch(() => {
        if (!cancelled) setDetail(null)
      })

    return () => {
      cancelled = true
    }
  }, [chosen])

  const targets = (kind === 'role' ? detail?.roles : detail?.channels) ?? []
  const target = targets.some((t) => t.id === targetId) ? targetId : (targets[0]?.id ?? '')

  const forget = () => {
    setPreview(null)
    setProblem(null)
    setDone(null)
  }

  const show = () => {
    setBusy(true)
    forget()

    api
      .previewDiscordSnapshotRestore(chosen, kind, target)
      .then(setPreview)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not work out the change.'))
      .finally(() => setBusy(false))
  }

  const restore = () => {
    if (!preview) return

    setBusy(true)
    setProblem(null)
    setDone(null)

    api
      .restoreDiscordSnapshot(chosen, kind, target, preview.fingerprint)
      .then((result) => {
        if (result.error) setProblem(result.error)
        else setDone(`Put back ${result.applied.toLocaleString()} ${result.applied === 1 ? 'change' : 'changes'}.`)

        // The server is different now, so the change on screen is out of date.
        return api.previewDiscordSnapshotRestore(chosen, kind, target).then(setPreview)
      })
      .catch((e: unknown) => {
        setPreview(null)
        setProblem(e instanceof ApiError ? e.message : 'Could not restore.')
      })
      .finally(() => setBusy(false))
  }

  if (!data.restoreOn) {
    return (
      <Panel title="Restore">
        <EmptyRow>Off</EmptyRow>
      </Panel>
    )
  }

  return (
    <Panel title="Restore">
      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-end gap-3">
          <SnapshotSelect
            label="Snapshot"
            value={chosen}
            onChange={(v) => {
              setSnapshotId(v)
              forget()
            }}
            snapshots={snapshots}
          />

          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Restore</span>
            <Select
              value={kind}
              onChange={(v) => {
                setKind(v as 'role' | 'channel')
                forget()
              }}
              aria-label="Restore"
            >
              <option value="role">A role</option>
              <option value="channel">A channel</option>
            </Select>
          </label>

          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">{kind === 'role' ? 'Role' : 'Channel'}</span>
            <Select
              value={target}
              onChange={(v) => {
                setTargetId(v)
                forget()
              }}
              aria-label={kind === 'role' ? 'Role' : 'Channel'}
            >
              {targets.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </Select>
          </label>

          <Button type="button" size="xs" variant="outline" onClick={show} disabled={busy || target === ''}>
            Preview
          </Button>
        </div>

        {preview?.problem && <Notice tone="warn">{preview.problem}</Notice>}

        {preview && !preview.problem && preview.nothingToChange && (
          <>
            {preview.lines.length > 0 && (
              <ul className="flex flex-col gap-1">
                {preview.lines.map((line) => (
                  <li key={line}>{line}</li>
                ))}
              </ul>
            )}
            <EmptyRow>Nothing to change.</EmptyRow>
          </>
        )}

        {preview && !preview.problem && !preview.nothingToChange && (
          <div className="flex flex-col gap-3">
            <ul className="flex flex-col gap-1">
              {preview.lines.map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>
            <div>
              <Button type="button" size="xs" variant="destructive" onClick={restore} disabled={busy}>
                {busy ? 'Restoring…' : 'Restore'}
              </Button>
            </div>
          </div>
        )}

        <Outcome tone="ok">{done}</Outcome>
        {problem && <Notice tone="warn">{problem}</Notice>}
      </div>
    </Panel>
  )
}
