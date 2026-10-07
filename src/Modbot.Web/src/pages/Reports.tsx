import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { SwitchBank } from '@/components/ui/switch-bank'
import { Textarea } from '@/components/ui/textarea'
import { Row } from '@/components/ui/fact-row'
import { ago } from '@/lib/format'
import { api, ApiError, type CurrentUser, type MemberReportList, type MemberReportView } from '@/lib/api'
import { can } from '@/lib/permissions'
import {
  aboutSubject,
  attachmentLine,
  closeProblem,
  CLOSE_NOTE_MAX,
  discordLink,
  hasQuote,
  openTabCount,
  personName,
  writtenText,
} from '@/lib/reports'
import { openSubject } from '@/lib/subject'

/**
 * What members told the mods with `/report` or Report to mods in Discord.
 *
 * Everyone who can open this page sees who reported (decision 2); Discord, the alerts and the audit
 * log never do. A report about a staff account is left out by the server unless this person also
 * holds Review tickets, so nothing here has to hide it. Closing needs a note, the same as Reviews.
 */
export function Reports({
  me,
  onChanged,
}: {
  me: CurrentUser
  /** Called after a report closes and whenever the list is read, so the nav badge can catch up. */
  onChanged?: () => void
}) {
  const [state, setState] = useState<'open' | 'closed'>('open')
  const [list, setList] = useState<MemberReportList | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    return api
      .memberReports(state)
      .then((next) => {
        setList(next)
        setError(null)
        onChanged?.()
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see reports.'
            : 'Could not load the reports.',
        ),
      )
  }, [state, onChanged])

  useEffect(() => {
    load()
  }, [load])

  if (error) {
    return (
      <Card>
        <EmptyRow tone="danger" onTryAgain={load}>{error}</EmptyRow>
      </Card>
    )
  }

  const open = openTabCount(list?.openCount)

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <SwitchBank
          value={state}
          onChange={setState}
          options={[
            {
              value: 'open',
              label:
                open === null ? (
                  'Open'
                ) : (
                  <>
                    Open <span className="font-mono">{open}</span>
                  </>
                ),
            },
            { value: 'closed', label: 'Closed' },
          ]}
        />
      </div>

      {!list && (
        <Card>
          <EmptyRow tone="loading" />
        </Card>
      )}

      {list && list.reports.length === 0 && (
        <Card>
          <EmptyRow>{state === 'open' ? 'Nothing open.' : 'Nothing closed yet.'}</EmptyRow>
        </Card>
      )}

      {list?.reports.map((report) => (
        <ReportCard key={report.id} report={report} now={list.now} me={me} onClosed={load} />
      ))}
    </div>
  )
}

function ReportCard({
  report,
  now,
  me,
  onClosed,
}: {
  report: MemberReportView
  now: string
  me: CurrentUser
  onClosed: () => void
}) {
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const subject = aboutSubject(report.about)
  const written = writtenText(report)
  const link = discordLink(report)
  const files = attachmentLine(report.message)
  const seesProfiles = can(me, 'ViewProfile')
  const writesNotes = can(me, 'WriteNotes')
  const handles = can(me, 'HandleReports')

  const close = () => {
    const why = closeProblem(note)
    if (why) {
      setProblem(why)
      return
    }

    setBusy(true)
    setProblem(null)
    api
      .closeMemberReport(report.id, note.trim())
      .then(() => onClosed())
      .catch((e: unknown) =>
        setProblem(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to close reports.'
            : e instanceof ApiError && e.status === 409
              ? 'Somebody else closed this report a moment ago.'
              : 'Could not close the report.',
        ),
      )
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader className="items-baseline">
        <CardTitle>
          <button
            type="button"
            className="rounded-sm text-left hover:underline focus-visible:outline-2 focus-visible:outline-ring"
            title={report.about.discordId}
            onClick={() => openSubject(subject)}
          >
            {personName(report.about)}
          </button>
        </CardTitle>
        <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span className="font-mono">{ago(report.createdAt, now)}</span>
        </span>
      </CardHeader>

      <CardContent>
        {written ? (
          <p className="max-w-3xl break-words whitespace-pre-wrap">{written}</p>
        ) : (
          <p className="text-muted-foreground">Removed.</p>
        )}

        {hasQuote(report) && report.message && (
          <blockquote
            className="mt-2 max-w-3xl border-l-2 pl-3 text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <div className="font-mono" style={{ fontSize: 'var(--text-tiny)' }}>
              {report.message.channelName ? `#${report.message.channelName}` : null}
              {report.message.channelName && report.message.sentAt ? ' · ' : null}
              {report.message.sentAt ? ago(report.message.sentAt, now) : null}
            </div>
            {report.message.text && <p className="break-words whitespace-pre-wrap">{report.message.text}</p>}
            {files && <p className="font-mono">{files}</p>}
          </blockquote>
        )}

        <div className="mt-2 max-w-lg text-foreground">
          <Row
            label="Reported by"
            value={
              <>
                {personName(report.reporter)}{' '}
                <span className="font-mono text-muted-foreground">{report.reporter.discordId}</span>
              </>
            }
          />
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-2">
          {link && (
            <Button size="xs" variant="outline" asChild>
              <a href={link} target="_blank" rel="noreferrer noopener">
                Open in Discord
              </a>
            </Button>
          )}
          {writesNotes && (
            <Button size="xs" variant="outline" onClick={() => openSubject(subject, { tab: 'notes' })}>
              Add note
            </Button>
          )}
          {writesNotes && (
            <Button size="xs" variant="outline" onClick={() => openSubject(subject, { tab: 'notes' })}>
              Watch
            </Button>
          )}
          {seesProfiles && (
            <Button size="xs" variant="outline" onClick={() => openSubject(subject)}>
              Profile
            </Button>
          )}
        </div>

        {report.state === 'open' && handles && (
          <label className="mt-3 flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Close note</span>
            <Textarea
              className="min-h-16"
              value={note}
              onChange={(e) => setNote(e.target.value)}
              maxLength={CLOSE_NOTE_MAX}
            />
          </label>
        )}
      </CardContent>

      {report.state === 'closed' ? (
        <CardFooter className="block" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">{report.closedByUsername ?? 'Somebody'} wrote: </span>
          <span className="break-words whitespace-pre-wrap">{report.closeNote}</span>
        </CardFooter>
      ) : (
        handles && (
          <CardFooter className="flex-wrap gap-2">
            <Button size="xs" onClick={close} disabled={busy}>
              Close
            </Button>
            {problem && (
              <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                {problem}
              </span>
            )}
          </CardFooter>
        )
      )}
    </Card>
  )
}
