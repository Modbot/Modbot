import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Pager } from '@/components/Pager'
import { EmptyRow } from '@/components/PanelGrid'
import { SubjectLink } from '@/components/facts'
import { RuleBuilder } from '@/components/giveaways/RuleBuilder'
import { Field, Outcome } from '@/components/settings/fields'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Row } from '@/components/ui/fact-row'
import { SwitchBank } from '@/components/ui/switch-bank'
import { dateTime } from '@/components/charts/format'
import { ApiError } from '@/lib/api'
import type { GiveawayBuilder, GiveawayRule } from '@/lib/giveaways'
import { usePageState } from '@/lib/listPage'
import { listApi, type ListPeople, type ListPerson, type SavedList, type SavedLists } from '@/lib/lists'
import { EXPORT_FORMATS, EXPORT_LABEL, countWords, peopleWords, type ExportFormat } from '@/lib/listWords'
import { go, useLocation } from '@/lib/router'
import { useShortcuts } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'
import { PageMessage } from '@/pages/analytics/shared'

const PAGE_SIZE = 50

/** The columns an export carries, as the export dialog names them. */
const EXPORT_COLUMNS = [
  'Name',
  'VRChat id',
  'Discord id',
  'Linked',
  'In the group',
  'Joined the group',
  'In Discord',
  'Joined Discord',
  'Trust rank',
  '18+ verified',
  'VRChat account made',
  'First seen',
]

/**
 * Lists (lists design): saved lists of people, each a name and the giveaway rules, asked again
 * every time one is opened. Who is in each, a page at a time, and a copy of them as a file.
 */
export function Lists() {
  // `?list=` opens one, so a list can be sent to another moderator. Read once, as the page's
  // opening state, so closing the dialog does not reopen it on the next render.
  const [location] = useLocation()

  const [data, setData] = useState<SavedLists | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(() => location.search.get('list'))
  const [editing, setEditing] = useState<SavedList | 'new' | null>(null)

  useShortcuts(data?.canManage ? [{ label: 'New list', group: 'Page', page: true, run: () => setEditing('new') }] : [])

  const load = useCallback(() => {
    listApi
      .all()
      .then((answer) => {
        setData(answer)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the lists.',
        ),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  const opened = data.lists.find((l) => l.id === openId) ?? null

  return (
    <div className="flex flex-col gap-3">
      {data.canManage && (
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex-1" />
          <Button onClick={() => setEditing('new')}>New list</Button>
        </div>
      )}

      {error && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}

      {data.lists.length === 0 ? (
        <PageMessage>No lists.</PageMessage>
      ) : (
        <Card className="divide-y-(--hairline) divide-border">
          {data.lists.map((list) => (
            <ListRow key={list.id} list={list} onOpen={() => setOpenId(list.id)} />
          ))}
        </Card>
      )}

      {opened && (
        <ListDialog
          list={opened}
          canManage={data.canManage}
          canGiveRoles={data.canGiveRoles ?? false}
          onClose={() => setOpenId(null)}
          onEdit={() => {
            setEditing(opened)
            setOpenId(null)
          }}
          onDeleted={() => {
            setOpenId(null)
            load()
          }}
        />
      )}

      {editing && (
        <ListForm
          list={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={(saved) => {
            setEditing(null)
            load()
            setOpenId(saved.id)
          }}
        />
      )}
    </div>
  )
}

/** One list in the page's list: its name, how many people are in it now, and what uses it. */
function ListRow({ list, onOpen }: { list: SavedList; onOpen: () => void }) {
  const [count, setCount] = useState<ListPeople | null>(null)

  // Each row asks for its own count, so the page draws at once and a slow list does not hold up
  // the rest (M7 §5: the count is worked out apart from any page of people).
  useEffect(() => {
    let cancelled = false
    listApi
      .people(list.id, 1, 1)
      .then((answer) => {
        if (!cancelled) setCount(answer)
      })
      .catch(() => {
        if (!cancelled) setCount(null)
      })
    return () => {
      cancelled = true
    }
  }, [list.id, list.updatedAt])

  return (
    <div className="flex min-h-(--row-h) flex-wrap items-center gap-x-3 gap-y-1 px-(--panel-pad) py-1.5">
      <button type="button" className="font-medium hover:underline" onClick={onOpen}>
        {list.name}
      </button>

      <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {count === null ? '…' : count.unanswerable ? '—' : <span className="font-mono">{peopleWords(count)}</span>}
      </span>

      <div className="flex-1" />

      <UseBadges use={list.usedBy} />
    </div>
  )
}

function UseBadges({ use }: { use: SavedList['usedBy'] }) {
  return (
    <span className="flex flex-wrap gap-1">
      {use.giveaways.map((name) => (
        <Badge key={name} variant="secondary">
          Giveaway: {name}
        </Badge>
      ))}
      {use.autoInvites && <Badge variant="secondary">Auto-invites</Badge>}
      {(use.events ?? []).map((title, i) => (
        <Badge key={`event:${i}`} variant="secondary">
          Event: {title}
        </Badge>
      ))}
      {(use.discordRoles ?? []).map((name, i) => (
        <Badge key={`role:${i}`} variant="secondary">
          Discord role: {name}
        </Badge>
      ))}
    </span>
  )
}

/** One list: its rules, who is in it, a page at a time, and the buttons. */
function ListDialog({
  list,
  canManage,
  canGiveRoles,
  onClose,
  onEdit,
  onDeleted,
}: {
  list: SavedList
  canManage: boolean
  canGiveRoles: boolean
  onClose: () => void
  onEdit: () => void
  onDeleted: () => void
}) {
  const at = usePageState()
  const [people, setPeople] = useState<ListPeople | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [exporting, setExporting] = useState(false)
  // Bumped by Try again, so a failed read is asked again without redrawing the rest of the dialog.
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false
    listApi
      .people(list.id, at.page, PAGE_SIZE)
      .then((answer) => {
        if (cancelled) return
        setPeople(answer)
        setProblem(null)
      })
      .catch((e: unknown) => {
        if (!cancelled) setProblem(e instanceof ApiError ? e.message : 'Could not work out who is in it.')
      })
    return () => {
      cancelled = true
    }
  }, [list.id, list.updatedAt, at.page, attempt])

  const inUse =
    list.usedBy.giveaways.length > 0 ||
    list.usedBy.autoInvites ||
    (list.usedBy.events ?? []).length > 0 ||
    (list.usedBy.discordRoles ?? []).length > 0

  return (
    <>
      <Dialog open onOpenChange={(open) => !open && onClose()}>
        <DialogContent
          title={list.name}
          className="max-w-[860px]"
          bodyClassName="max-h-[78vh] overflow-y-auto"
          foot={
            <DialogFoot>
              {canManage && (
                <Button size="sm" variant="destructive" disabled={inUse} onClick={() => setDeleting(true)}>
                  Delete
                </Button>
              )}
              {canManage && (
                <Button size="sm" variant="outline" onClick={onEdit}>
                  Edit
                </Button>
              )}
              {canGiveRoles && (
                <Button size="sm" variant="outline" onClick={() => go(`/settings#discord/lists/${encodeURIComponent(list.id)}`)}>
                  Give a Discord role
                </Button>
              )}
              <Button
                size="sm"
                disabled={!people || people.unanswerable !== null || people.count === 0}
                onClick={() => setExporting(true)}
              >
                Export
              </Button>
            </DialogFoot>
          }
        >
          <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
            <Section title="Rules">
              <ul className="flex flex-col gap-0.5">
                {list.ruleLines.map((line, i) => (
                  <li key={i}>{line}</li>
                ))}
              </ul>
            </Section>

            {inUse && (
              <Section title="Used by">
                <UseBadges use={list.usedBy} />
              </Section>
            )}

            <Section title="Who is in it">
              {problem ? (
                <EmptyRow className="px-0" tone="danger" onTryAgain={() => setAttempt((n) => n + 1)}>
                  {problem}
                </EmptyRow>
              ) : !people ? (
                <EmptyRow className="px-0" tone="loading" />
              ) : people.unanswerable ? (
                <span className="text-destructive">{people.unanswerable}</span>
              ) : (
                <>
                  <CountLine people={people} />
                  {people.people.length > 0 && (
                    <Card>
                      <PeopleTable people={people.people} />
                      <Pager at={at} pages={Math.max(1, Math.ceil(people.count / PAGE_SIZE))} />
                    </Card>
                  )}
                </>
              )}
            </Section>

            <div className="max-w-lg text-foreground">
              <Row label="Made" value={<>{dateTime(list.createdAt)}{list.createdBy && ` by ${list.createdBy}`}</>} />
              <Row label="Changed" value={dateTime(list.updatedAt)} mono />
            </div>
          </div>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title={`Delete the list “${list.name}”?`}
        action="Delete"
        failed="Could not delete the list."
        onConfirm={() => listApi.remove(list.id)}
        onDone={onDeleted}
      />

      {exporting && people && (
        <ExportDialog
          list={list}
          count={people}
          onClose={() => setExporting(false)}
        />
      )}
    </>
  )
}

/** "about 312 people of 4,081 · 3 near the line · at 14:02". */
function CountLine({ people }: { people: ListPeople }) {
  return (
    <span>
      <span className="font-mono">{countWords(people)}</span> of{' '}
      <span className="font-mono">{people.considered.toLocaleString()}</span>
      {people.closeCalls > 0 && (
        <>
          {' · '}
          <span className="font-mono">{people.closeCalls}</span> near the line
        </>
      )}
      <span className="text-muted-foreground">
        {' · '}
        <span className="font-mono">{dateTime(people.countedAt)}</span>
      </span>
    </span>
  )
}

function PeopleTable({ people }: { people: ListPerson[] }) {
  return (
    <Table
      nameColumn={0}
      head={
        <>
          <Th>Name</Th>
          <Th>In the group</Th>
          <Th>In Discord</Th>
        </>
      }
    >
      {people.map((p) => (
        <Tr key={p.key}>
          <Td className={cn(p.closeCall && 'text-warn')} title={p.closeCall ? 'Near the line' : undefined}>
            {p.vrChatUserId ? <SubjectLink id={p.vrChatUserId} name={p.name ?? p.key} /> : <span>{p.name ?? p.key}</span>}
          </Td>
          <Td>{p.inGroup ? 'Yes' : '—'}</Td>
          <Td>{p.inDiscord ? 'Yes' : '—'}</Td>
        </Tr>
      ))}
    </Table>
  )
}

/**
 * The export: which file, how many people, and the columns it will carry, before anything is
 * made. The file leaves Modbot, so the dialog names what goes into it rather than a button doing
 * it in one tap.
 */
function ExportDialog({
  list,
  count,
  onClose,
}: {
  list: SavedList
  count: ListPeople
  onClose: () => void
}) {
  const [format, setFormat] = useState<ExportFormat>('csv')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const run = () => {
    setBusy(true)
    setProblem(null)
    listApi
      .exportFile(list.id, format, 'list')
      .then(onClose)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not export the list.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={`Export “${list.name}”`}
        className="max-w-[520px]"
        foot={
          <DialogFoot>
            <Button size="sm" variant="outline" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button size="sm" onClick={run} disabled={busy}>
              {busy ? 'Exporting…' : 'Export'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
          <SwitchBank
            value={format}
            onChange={setFormat}
            options={EXPORT_FORMATS.map((f) => ({ value: f, label: EXPORT_LABEL[f] }))}
          />
          <div className="text-foreground">
            <Row label="People" value={<span className="font-mono">{countWords(count)}</span>} />
            <Row label="Columns" value={EXPORT_COLUMNS.join(', ')} />
          </div>
          {problem && <Outcome tone="problem">{problem}</Outcome>}
        </div>
      </DialogContent>
    </Dialog>
  )
}

/** The create and edit form: a name, the rule builder, and a preview of who it lets through. */
function ListForm({
  list,
  onClose,
  onSaved,
}: {
  list: SavedList | null
  onClose: () => void
  onSaved: (saved: SavedList) => void
}) {
  const [name, setName] = useState(list?.name ?? '')
  const [rules, setRules] = useState<GiveawayRule>(list?.rules ?? { kind: 'allOf', rules: [] })
  const [builder, setBuilder] = useState<GiveawayBuilder | null>(null)
  const [preview, setPreview] = useState<ListPeople | null>(null)
  const [previewProblem, setPreviewProblem] = useState<string | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    listApi
      .builder()
      .then(setBuilder)
      .catch(() => setBuilder(null))
  }, [])

  const runPreview = () => {
    setPreviewing(true)
    setPreviewProblem(null)
    listApi
      .preview(rules)
      .then(setPreview)
      .catch((e: unknown) => {
        setPreview(null)
        setPreviewProblem(e instanceof ApiError ? e.message : 'Could not work that out.')
      })
      .finally(() => setPreviewing(false))
  }

  const save = () => {
    setBusy(true)
    setError(null)
    const body = { name, rules }
    const request = list ? listApi.update(list.id, body) : listApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the list.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={list ? 'Edit list' : 'New list'}
        className="max-w-[860px]"
        bodyClassName="max-h-[78vh] overflow-y-auto"
        foot={
          <DialogFoot>
            {error && <Outcome tone="problem">{error}</Outcome>}
            <Button size="sm" variant="outline" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button size="sm" onClick={save} disabled={busy}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-4">
          <Field label="Name" value={name} maxLength={100} onChange={setName} />

          <Section title="Rules">
            <RuleBuilder rule={rules} builder={builder} onChange={setRules} />
            <div className="flex flex-wrap items-center gap-3" style={{ fontSize: 'var(--text-small)' }}>
              <Button type="button" variant="outline" size="sm" disabled={previewing} onClick={runPreview}>
                Preview
              </Button>
              {previewProblem ? (
                <span className="text-destructive">{previewProblem}</span>
              ) : preview?.unanswerable ? (
                <span className="text-destructive">{preview.unanswerable}</span>
              ) : preview ? (
                <CountLine people={preview} />
              ) : null}
            </div>
            {preview && !preview.unanswerable && preview.people.length > 0 && (
              <Card>
                <PeopleTable people={preview.people} />
              </Card>
            )}
          </Section>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-2 border-t border-t-(length:--hairline) pt-3">
      <span className="font-label">{title}</span>
      {children}
    </div>
  )
}
