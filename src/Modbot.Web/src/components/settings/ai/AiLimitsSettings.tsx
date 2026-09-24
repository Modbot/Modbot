import { useCallback, useEffect, useId, useState } from 'react'
import { DailyBars, Legend, nextSlot, type DaySeries } from '@/components/charts'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { amountText, count, money, share, spentText, tokensText, tokensTitle } from '@/lib/aiSpend'
import {
  api,
  ApiError,
  type AiFeatureSpend,
  type AiFetchedPrice,
  type AiLimit,
  type AiLimitAppliesTo,
  type AiLimits,
  type AiPrice,
  type AiSpent,
} from '@/lib/api'
import { ago } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Outcome, Placeholder } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/** A number box's text as a number, or null when empty. NaN when it is not a number. */
function amount(text: string): number | null {
  return text.trim() === '' ? null : Number(text)
}

const failure = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'You do not have permission to change AI settings.'
    : e instanceof ApiError
      ? e.message
      : 'Could not reach the Modbot server.'

/** Runs the card's content to its edges, so a table meets the card's sides. */
const FLUSH = '[&>[data-slot=card-content]]:gap-0 [&>[data-slot=card-content]]:p-0'
const cellClass = 'px-(--panel-pad) py-1.5 align-top'
const headClass = 'h-(--row-h) px-(--panel-pad) font-normal whitespace-nowrap'
const rowClass = 'border-b border-b-(length:--hairline) last:border-0'

/**
 * Settings → AI → Limits: what AI has cost by feature, the month-end estimate, the spend limits for
 * everyone, a feature, a role or one account, and the price list (AI chat design §10).
 */
export function AiLimitsSettings() {
  const [data, setData] = useState<AiLimits | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .aiLimits()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change AI settings.'
              : 'Could not load AI limits.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="ai-limits" title="AI limits">
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <SpendCard data={data} />
          <DailyCard data={data} />
          <LimitsCard key={`limits-${JSON.stringify([data.limits, data.tokenLimits])}`} data={data} onSaved={setData} />
          <TopUsersCard data={data} />
          <PricesCard key={`prices-${JSON.stringify(data.models)}`} data={data} onSaved={setData} />
        </>
      )}
    </SettingsSection>
  )
}

/** Money on one line and tokens under it. */
function Spent({ spent, of }: { spent: AiSpent | null; of?: string }) {
  if (!spent) return <span className="text-muted-foreground">—</span>

  return (
    <div className="font-mono whitespace-nowrap" title={tokensTitle(spent)}>
      <div>
        {spentText(spent)}
        {of && <span className="text-muted-foreground"> · {of}</span>}
      </div>
      <div className="text-muted-foreground">{tokensText(spent)}</div>
    </div>
  )
}

function SpendCard({ data }: { data: AiLimits }) {
  const rows: AiFeatureSpend[] = [...data.spend, data.total]

  return (
    <SettingsCard title="Spend by feature" span={12} className={FLUSH}>
      <div className="relative overflow-x-auto">
        <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
          <thead className="bg-strip text-left text-muted-foreground">
            <tr className="border-b border-b-(length:--hairline)">
              <th className={headClass}>Feature</th>
              <th className={cn(headClass, 'text-right')}>Today</th>
              <th className={cn(headClass, 'text-right')}>This week</th>
              <th className={cn(headClass, 'text-right')}>This month</th>
              <th className={cn(headClass, 'text-right')}>Last month</th>
              <th className={cn(headClass, 'text-right')}>Month-end estimate</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.feature} className={cn(rowClass, r === data.total && 'font-medium')}>
                <td className={cn(cellClass, 'whitespace-nowrap')}>{r.label}</td>
                <td className={cn(cellClass, 'text-right')}>
                  <Spent spent={r.today} />
                </td>
                <td className={cn(cellClass, 'text-right')}>
                  <Spent spent={r.week} />
                </td>
                <td className={cn(cellClass, 'text-right')}>
                  <Spent spent={r.month} />
                </td>
                <td className={cn(cellClass, 'text-right')}>
                  <Spent spent={r.lastMonth} />
                </td>
                <td className={cn(cellClass, 'text-right')}>
                  <Spent spent={r.estimate} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </SettingsCard>
  )
}

function DailyCard({ data }: { data: AiLimits }) {
  // One series per feature, in the order the server lists them, so a feature keeps its colour.
  const series: DaySeries[] = data.spend.map((f, i) => ({
    key: f.feature,
    label: f.label,
    slot: nextSlot(i),
    points: data.days.filter((d) => d.feature === f.feature).map((d) => ({ day: d.day, value: d.cost })),
  }))

  const partUnknown = data.days.some((d) => d.unpricedTokens > 0)

  return (
    <SettingsCard
      title="Daily spend"
      span={12}
      className={cn(partUnknown && '[&>[data-slot=card-header]]:bg-warn/10')}
      action={
        partUnknown ? (
          <span className="flex items-center gap-1.5 text-warn" style={{ fontSize: 'var(--text-small)' }}>
            <span aria-hidden className="size-2 shrink-0 bg-warn" />
            Part unknown
          </span>
        ) : undefined
      }
    >
      <Legend items={series.map((s) => ({ label: s.label, slot: s.slot }))} />
      <DailyBars from={data.firstDay} to={data.lastDay} series={series} stacked format={money} emptyText="No AI spend." />
    </SettingsCard>
  )
}

type LimitRow = {
  appliesTo: AiLimitAppliesTo
  feature: string | null
  roleId: string | null
  userId: string | null
  name: string | null
  perDay: string
  perMonth: string
  today: AiSpent | null
  month: AiSpent | null
  estimate: AiSpent | null
}

type TokenRow = { feature: string; monthlyTokens: number; month: AiSpent | null; estimate: AiSpent | null }

const rowKey = (r: { appliesTo: string; feature: string | null; roleId: string | null; userId: string | null }) =>
  `${r.appliesTo}:${r.feature ?? r.roleId ?? r.userId ?? ''}`

function toRow(l: AiLimit): LimitRow {
  return {
    ...l,
    perDay: l.perDay === null ? '' : String(l.perDay),
    perMonth: l.perMonth === null ? '' : String(l.perMonth),
  }
}

function LimitsCard({ data, onSaved }: { data: AiLimits; onSaved: (next: AiLimits) => void }) {
  const [rows, setRows] = useState<LimitRow[]>(() => data.limits.map(toRow))
  const [tokenRows, setTokenRows] = useState<TokenRow[]>(() => data.tokenLimits)
  const [adding, setAdding] = useState('')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const taken = new Set(rows.map(rowKey))
  const featureLabel = (id: string | null) => data.features.find((f) => f.id === id)?.label ?? id ?? ''

  const add = () => {
    const [appliesTo, id] = adding.split(':') as [AiLimitAppliesTo, string]
    if (!appliesTo) return

    const name =
      appliesTo === 'role'
        ? (data.roles.find((r) => r.id === id)?.name ?? null)
        : appliesTo === 'user'
          ? (data.users.find((u) => u.id === id)?.name ?? null)
          : appliesTo === 'feature'
            ? featureLabel(id)
            : null

    const spend = appliesTo === 'feature' ? data.spend.find((s) => s.feature === id) : undefined

    setRows((all) => [
      ...all,
      {
        appliesTo,
        feature: appliesTo === 'feature' ? id : null,
        roleId: appliesTo === 'role' ? id : null,
        userId: appliesTo === 'user' ? id : null,
        name,
        perDay: '',
        perMonth: '',
        today: appliesTo === 'everyone' ? data.total.today : (spend?.today ?? null),
        month: appliesTo === 'everyone' ? data.total.month : (spend?.month ?? null),
        estimate: appliesTo === 'everyone' ? data.total.estimate : (spend?.estimate ?? null),
      },
    ])
    setAdding('')
  }

  const save = () => {
    const limits = rows.map((r) => ({
      appliesTo: r.appliesTo,
      feature: r.feature,
      roleId: r.roleId,
      userId: r.userId,
      perDay: amount(r.perDay),
      perMonth: amount(r.perMonth),
    }))

    if (limits.some((l) => Number.isNaN(l.perDay) || Number.isNaN(l.perMonth))) {
      setProblem('A limit must be a number.')
      return
    }

    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setAiLimits(
        limits,
        tokenRows.map((t) => ({ feature: t.feature, monthlyTokens: t.monthlyTokens })),
      )
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(false))
  }

  const label = (r: LimitRow) =>
    r.appliesTo === 'everyone'
      ? 'Everyone'
      : r.appliesTo === 'feature'
        ? featureLabel(r.feature)
        : r.appliesTo === 'role'
          ? `Role · ${r.name ?? ''}`
          : `User · ${r.name ?? ''}`

  const set = (i: number, patch: Partial<LimitRow>) =>
    setRows((all) => all.map((x, j) => (j === i ? { ...x, ...patch } : x)))

  return (
    <SettingsCard
      title="Spend limits"
      span={12}
      className={FLUSH}
      footer={
        <>
          <Button size="sm" disabled={busy} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {(rows.length > 0 || tokenRows.length > 0) && (
        <div className="relative overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="bg-strip text-left text-muted-foreground">
              <tr className="border-b border-b-(length:--hairline)">
                <th className={headClass}>Applies to</th>
                <th className={headClass}>Per day (USD)</th>
                <th className={headClass}>Per month (USD)</th>
                <th className={cn(headClass, 'text-right')}>Spent today</th>
                <th className={cn(headClass, 'text-right')}>Spent this month</th>
                <th className={cn(headClass, 'text-right')}>Month-end estimate</th>
                <th className={headClass} />
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => {
                const perDay = amount(r.perDay)
                const perMonth = amount(r.perMonth)
                const eachMember = r.appliesTo === 'role'

                return (
                  <tr key={rowKey(r)} className={rowClass}>
                    <td className={cn(cellClass, 'whitespace-nowrap')}>{label(r)}</td>
                    <td className={cellClass}>
                      <Money label={`${label(r)} per day`} value={r.perDay} onChange={(v) => set(i, { perDay: v })} />
                    </td>
                    <td className={cellClass}>
                      <Money label={`${label(r)} per month`} value={r.perMonth} onChange={(v) => set(i, { perMonth: v })} />
                    </td>
                    <td className={cn(cellClass, 'text-right')}>
                      {eachMember ? 'Each member' : <Spent spent={r.today} of={r.today ? share(r.today.cost, perDay) : ''} />}
                    </td>
                    <td className={cn(cellClass, 'text-right')}>
                      {eachMember ? 'Each member' : <Spent spent={r.month} of={r.month ? share(r.month.cost, perMonth) : ''} />}
                    </td>
                    <td className={cn(cellClass, 'text-right')}>
                      {r.estimate ? <Spent spent={r.estimate} of={share(r.estimate.cost, perMonth)} /> : <span className="text-muted-foreground">—</span>}
                    </td>
                    <td className={cn(cellClass, 'text-right')}>
                      <Button size="sm" variant="ghost" onClick={() => setRows((all) => all.filter((_, j) => j !== i))}>
                        Remove
                      </Button>
                    </td>
                  </tr>
                )
              })}
              {tokenRows.map((t, i) => (
                <tr key={`tokens:${t.feature}`} className={rowClass}>
                  <td className={cn(cellClass, 'whitespace-nowrap')}>{featureLabel(t.feature)} · tokens</td>
                  <td className={cn(cellClass, 'text-muted-foreground')}>—</td>
                  <td className={cn(cellClass, 'whitespace-nowrap font-mono')}>{amountText(t.monthlyTokens, 'tokens')}</td>
                  <td className={cn(cellClass, 'text-right text-muted-foreground')}>—</td>
                  <td className={cn(cellClass, 'text-right font-mono whitespace-nowrap')}>
                    {t.month && `${tokensText(t.month)} · ${share(t.month.inputTokens + t.month.outputTokens, t.monthlyTokens)}`}
                  </td>
                  <td className={cn(cellClass, 'text-right font-mono whitespace-nowrap')}>
                    {t.estimate &&
                      `${tokensText(t.estimate)} · ${share(t.estimate.inputTokens + t.estimate.outputTokens, t.monthlyTokens)}`}
                  </td>
                  <td className={cn(cellClass, 'text-right')}>
                    <Button size="sm" variant="ghost" onClick={() => setTokenRows((all) => all.filter((_, j) => j !== i))}>
                      Remove
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div
        className={cn(
          'flex flex-wrap items-center gap-2 p-(--panel-pad)',
          (rows.length > 0 || tokenRows.length > 0) && 'border-t border-t-(length:--hairline)',
        )}
      >
        <Select aria-label="Add a limit for" value={adding} onChange={setAdding}>
          <option value="">Add a limit for…</option>
          {!taken.has('everyone:') && <option value="everyone:">Everyone</option>}
          <optgroup label="Features">
            {data.features
              .filter((f) => !taken.has(`feature:${f.id}`))
              .map((f) => (
                <option key={f.id} value={`feature:${f.id}`}>
                  {f.label}
                </option>
              ))}
          </optgroup>
          <optgroup label="Chat · roles">
            {data.roles
              .filter((r) => !taken.has(`role:${r.id}`))
              .map((r) => (
                <option key={r.id} value={`role:${r.id}`}>
                  {r.name}
                </option>
              ))}
          </optgroup>
          <optgroup label="Chat · users">
            {data.users
              .filter((u) => !taken.has(`user:${u.id}`))
              .map((u) => (
                <option key={u.id} value={`user:${u.id}`}>
                  {u.name}
                </option>
              ))}
          </optgroup>
        </Select>
        <Button size="sm" variant="outline" disabled={!adding} onClick={add}>
          Add
        </Button>
      </div>
    </SettingsCard>
  )
}

function TopUsersCard({ data }: { data: AiLimits }) {
  return (
    <SettingsCard title="Top Chat users this month" span={12} className={FLUSH}>
      {data.topChatUsers.length === 0 ? (
        <EmptyRow>None.</EmptyRow>
      ) : (
        <div className="relative overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="bg-strip text-left text-muted-foreground">
              <tr className="border-b border-b-(length:--hairline)">
                <th className={headClass}>User</th>
                <th className={cn(headClass, 'text-right')}>Spent</th>
                <th className={cn(headClass, 'text-right')}>Tokens</th>
              </tr>
            </thead>
            <tbody>
              {data.topChatUsers.map((u) => (
                <tr key={u.userId} className={rowClass}>
                  <td className={cn(cellClass, 'whitespace-nowrap')}>{u.username ?? u.userId}</td>
                  <td className={cn(cellClass, 'text-right font-mono whitespace-nowrap')} title={tokensTitle(u.month)}>
                    {spentText(u.month)}
                  </td>
                  <td className={cn(cellClass, 'text-right font-mono whitespace-nowrap')}>
                    {count(u.month.inputTokens + u.month.outputTokens)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </SettingsCard>
  )
}

type PriceRow = { model: string; input: string; cached: string; output: string; fetched: AiFetchedPrice | null }

const text = (n: number | null | undefined) => (n === null || n === undefined ? '' : String(n))

function PricesCard({ data, onSaved }: { data: AiLimits; onSaved: (next: AiLimits) => void }) {
  const [rows, setRows] = useState<PriceRow[]>(() =>
    data.models.map((m) => ({
      model: m.model,
      input: text(m.entered?.inputPerMillion),
      cached: text(m.entered?.cachedInputPerMillion),
      output: text(m.entered?.outputPerMillion),
      fetched: m.fetched,
    })),
  )
  const [adding, setAdding] = useState('')
  const [busy, setBusy] = useState(false)
  const [fetching, setFetching] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const listId = useId()

  const set = (i: number, patch: Partial<PriceRow>) =>
    setRows((all) => all.map((x, j) => (j === i ? { ...x, ...patch } : x)))

  const add = () => {
    const model = adding.trim()
    if (!model || rows.some((r) => r.model === model)) return
    setRows((all) => [...all, { model, input: '', cached: '', output: '', fetched: null }])
    setAdding('')
  }

  const entered = (r: PriceRow) => [r.input, r.cached, r.output].some((v) => v.trim() !== '')

  const save = () => {
    const priced = rows.filter(entered)

    const missing = priced.find((r) => r.input.trim() === '' || r.output.trim() === '')
    if (missing) {
      setProblem(`Enter input and output prices for ${missing.model}.`)
      return
    }

    const prices: AiPrice[] = priced.map((r) => ({
      model: r.model,
      inputPerMillion: Number(r.input),
      cachedInputPerMillion: amount(r.cached),
      outputPerMillion: Number(r.output),
    }))

    if (prices.some((p) => [p.inputPerMillion, p.cachedInputPerMillion, p.outputPerMillion].some((n) => Number.isNaN(n)))) {
      setProblem('A price must be a number.')
      return
    }

    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setAiPrices(prices)
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(false))
  }

  const fetchPrices = () => {
    setFetching(true)
    setSaved(false)
    setProblem(null)

    api
      .fetchAiPrices()
      .then(onSaved)
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setFetching(false))
  }

  const source = (r: PriceRow) =>
    entered(r) ? 'Entered' : r.fetched ? `OpenRouter · ${ago(r.fetched.fetchedAt, data.now)}` : 'No price'

  return (
    <SettingsCard
      title="Prices per million tokens"
      span={12}
      className={FLUSH}
      footer={
        <>
          <Button size="sm" disabled={busy} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Button size="sm" variant="outline" disabled={fetching} onClick={fetchPrices}>
            {fetching ? 'Fetching…' : 'Fetch prices'}
          </Button>
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {data.pricesFetchedAt ? `Fetched ${ago(data.pricesFetchedAt, data.now)}` : 'Not fetched'}
          </span>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {rows.length > 0 && (
        <div className="relative overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="bg-strip text-left text-muted-foreground">
              <tr className="border-b border-b-(length:--hairline)">
                <th className={headClass}>Model</th>
                <th className={headClass}>Input (USD)</th>
                <th className={headClass}>Cached input (USD)</th>
                <th className={headClass}>Output (USD)</th>
                <th className={headClass}>Price from</th>
                <th className={headClass} />
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={r.model} className={rowClass}>
                  <td className={cn(cellClass, 'font-mono whitespace-nowrap')}>{r.model}</td>
                  <td className={cellClass}>
                    <Money
                      label={`${r.model} input`}
                      value={r.input}
                      placeholder={text(r.fetched?.inputPerMillion)}
                      onChange={(v) => set(i, { input: v })}
                    />
                  </td>
                  <td className={cellClass}>
                    <Money
                      label={`${r.model} cached input`}
                      value={r.cached}
                      placeholder={text(r.fetched?.cachedInputPerMillion)}
                      onChange={(v) => set(i, { cached: v })}
                    />
                  </td>
                  <td className={cellClass}>
                    <Money
                      label={`${r.model} output`}
                      value={r.output}
                      placeholder={text(r.fetched?.outputPerMillion)}
                      onChange={(v) => set(i, { output: v })}
                    />
                  </td>
                  <td
                    className={cn(
                      cellClass,
                      'whitespace-nowrap',
                      !entered(r) && !r.fetched ? 'text-warn' : 'text-muted-foreground',
                    )}
                  >
                    {source(r)}
                  </td>
                  <td className={cn(cellClass, 'text-right')}>
                    {entered(r) && (
                      <Button size="sm" variant="ghost" onClick={() => set(i, { input: '', cached: '', output: '' })}>
                        Clear
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div
        className={cn(
          'flex flex-wrap items-center gap-2 p-(--panel-pad)',
          rows.length > 0 && 'border-t border-t-(length:--hairline)',
        )}
      >
        <Input
          aria-label="Model"
          list={listId}
          value={adding}
          placeholder="Model"
          autoComplete="off"
          className="max-w-xs"
          onChange={(e) => setAdding(e.target.value)}
        />
        <datalist id={listId}>
          {data.modelsUsed
            .filter((m) => !rows.some((r) => r.model === m))
            .map((m) => (
              <option key={m} value={m} />
            ))}
        </datalist>
        <Button size="sm" variant="outline" disabled={!adding.trim()} onClick={add}>
          Add
        </Button>
      </div>
    </SettingsCard>
  )
}

function Money({
  label,
  value,
  placeholder,
  onChange,
}: {
  label: string
  value: string
  placeholder?: string
  onChange: (v: string) => void
}) {
  return (
    <Input
      aria-label={label}
      type="number"
      inputMode="decimal"
      min={0}
      step="any"
      value={value}
      placeholder={placeholder}
      className="w-28"
      onChange={(e) => onChange(e.target.value)}
    />
  )
}
