import { useCallback, useEffect, useState } from 'react'
import { AppealRow, LogAppealForm } from '@/components/AppealParts'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Row } from '@/components/ui/fact-row'
import { Select } from '@/components/ui/select'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ApiError } from '@/lib/api'
import {
  appealApi,
  dayOnly,
  type AppealList,
  type AppealOutcome,
  type YearlyReport,
} from '@/lib/appeals'

type View = 'appeals' | 'report'

/**
 * Appeals people sent to the team, and the year's moderation numbers.
 *
 * Staff only: reading needs See profiles, writing an appeal down and answering it need Unban. The
 * server says which the person has, so the page holds no permission check of its own.
 */
export function Appeals() {
  const [view, setView] = useState<View>('appeals')

  return (
    <div className="flex flex-col gap-3">
      <SwitchBank
        value={view}
        onChange={setView}
        options={[
          { value: 'appeals', label: 'Appeals' },
          { value: 'report', label: 'Yearly report' },
        ]}
      />
      {view === 'appeals' ? <AppealsList /> : <ReportView />}
    </div>
  )
}

const problemText = (e: unknown, what: string): string =>
  e instanceof ApiError && e.status === 403 ? 'You do not have permission to see this.' : `Could not load ${what}.`

function AppealsList() {
  const [year, setYear] = useState('')
  const [outcome, setOutcome] = useState<'' | AppealOutcome>('')
  const [years, setYears] = useState<number[]>([])
  const [list, setList] = useState<AppealList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [logging, setLogging] = useState(false)

  const load = useCallback(() => {
    return appealApi
      .list({ year: year ? Number(year) : undefined, outcome: outcome || undefined, limit: 200 })
      .then((next) => {
        setList(next)
        setError(null)
      })
      .catch((e: unknown) => setError(problemText(e, 'the appeals')))
  }, [year, outcome])

  useEffect(() => {
    load()
  }, [load])

  // The years worth picking come with the report; the list does not carry them.
  useEffect(() => {
    appealApi
      .report()
      .then((report) => setYears(report.years))
      .catch(() => setYears([]))
  }, [])

  const filtered = year !== '' || outcome !== ''

  return (
    <>
      <div className="flex flex-wrap items-center gap-2">
        <Select value={year} onChange={setYear} aria-label="Year">
          <option value="">All years</option>
          {years.map((y) => (
            <option key={y} value={String(y)}>
              {y}
            </option>
          ))}
        </Select>
        <Select value={outcome} onChange={(v) => setOutcome(v as '' | AppealOutcome)} aria-label="Outcome">
          <option value="">Any outcome</option>
          <option value="pending">Waiting</option>
          <option value="upheld">Upheld</option>
          <option value="turned-down">Turned down</option>
        </Select>
        <span className="flex-1" />
        {list?.canLog && !logging && (
          <Button size="sm" onClick={() => setLogging(true)}>
            Log appeal
          </Button>
        )}
      </div>

      {logging && list && (
        <Card>
          <CardHeader>
            <CardTitle>Log appeal</CardTitle>
          </CardHeader>
          <LogAppealForm
            now={list.now}
            onCancel={() => setLogging(false)}
            onLogged={() => {
              setLogging(false)
              load()
            }}
          />
        </Card>
      )}

      {error && (
        <Card>
          <EmptyRow tone="danger" onTryAgain={load}>{error}</EmptyRow>
        </Card>
      )}

      {!error && !list && (
        <Card>
          <EmptyRow tone="loading" />
        </Card>
      )}

      {!error && list && list.appeals.length === 0 && (
        <Card>
          <EmptyRow>{filtered ? 'No appeals match.' : 'No appeals yet.'}</EmptyRow>
        </Card>
      )}

      {!error && list && list.appeals.length > 0 && (
        <Card>
          <ul className="flex flex-col">
            {list.appeals.map((appeal) => (
              <AppealRow key={appeal.id} appeal={appeal} showPerson onChanged={load} />
            ))}
          </ul>
          <CardFooter className="gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span>
              <span className="font-mono">{list.appeals.length}</span> of <span className="font-mono">{list.total}</span>
            </span>
            <span>
              <span className="font-mono">{list.waiting}</span> waiting
            </span>
          </CardFooter>
        </Card>
      )}
    </>
  )
}

function ReportView() {
  const [year, setYear] = useState<number | undefined>(undefined)
  const [report, setReport] = useState<YearlyReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    return appealApi
      .report(year)
      .then((next) => {
        setReport(next)
        setError(null)
      })
      .catch((e: unknown) => setError(problemText(e, 'the report')))
  }, [year])

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

  if (!report) {
    return (
      <Card>
        <EmptyRow tone="loading" />
      </Card>
    )
  }

  return (
    <>
      <div className="flex flex-wrap items-center gap-2">
        <Select value={String(report.year)} onChange={(v) => setYear(Number(v))} aria-label="Year">
          {report.years.map((y) => (
            <option key={y} value={String(y)}>
              {y}
            </option>
          ))}
        </Select>
        <span className="flex-1" />
        <Button size="sm" variant="outline" asChild>
          <a href={appealApi.csvHref(report.year)} download>
            Download CSV
          </a>
        </Button>
      </div>

      <Card>
        <CardContent>
          <div className="max-w-lg">
            {report.lines.map((line) => (
              <Row key={line.key} label={line.label} value={line.count.toLocaleString()} mono />
            ))}
            {report.countedSince && <Row label="Counted since" value={dayOnly(report.countedSince)} mono />}
          </div>
        </CardContent>
      </Card>
    </>
  )
}
