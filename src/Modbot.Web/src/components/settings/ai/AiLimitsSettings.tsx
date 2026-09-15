import { useCallback, useEffect, useId, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  api,
  ApiError,
  type AiLimit,
  type AiLimitAppliesTo,
  type AiLimits,
  type AiPrice,
  type AiSpent,
} from '@/lib/api'
import { cn } from '@/lib/utils'
import { Fact, Outcome, Placeholder } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

const money = new Intl.NumberFormat(undefined, {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
})

const count = new Intl.NumberFormat()

function spentText(s: AiSpent | null): string {
  if (!s) return '—'
  return `${money.format(s.cost)} · ${count.format(s.inputTokens + s.outputTokens)} tokens`
}

function tokensTitle(s: AiSpent | null): string | undefined {
  if (!s) return undefined
  return `${count.format(s.inputTokens)} in (${count.format(s.cachedInputTokens)} cached) · ${count.format(s.outputTokens)} out`
}

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

/**
 * Settings → AI → Limits: what AI has cost, what each model costs, and the daily and monthly spend
 * limits for everyone, a role or one account (AI chat design §10).
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
          <SettingsCard title="Spent" span={12}>
            <div className="grid max-w-2xl grid-cols-2 gap-4">
              <div title={tokensTitle(data.today)}>
                <Fact label="Today" value={spentText(data.today)} />
              </div>
              <div title={tokensTitle(data.month)}>
                <Fact label="This month" value={spentText(data.month)} />
              </div>
            </div>
          </SettingsCard>
          <LimitsCard key={`limits-${JSON.stringify(data.limits)}`} data={data} onSaved={setData} />
          <PricesCard key={`prices-${JSON.stringify(data.prices)}`} data={data} onSaved={setData} />
        </>
      )}
    </SettingsSection>
  )
}

type LimitRow = {
  appliesTo: AiLimitAppliesTo
  roleId: string | null
  userId: string | null
  name: string | null
  perDay: string
  perMonth: string
  today: AiSpent | null
  month: AiSpent | null
}

const rowKey = (r: { appliesTo: string; roleId: string | null; userId: string | null }) =>
  `${r.appliesTo}:${r.roleId ?? r.userId ?? ''}`

function toRow(l: AiLimit): LimitRow {
  return {
    ...l,
    perDay: l.perDay === null ? '' : String(l.perDay),
    perMonth: l.perMonth === null ? '' : String(l.perMonth),
  }
}

function LimitsCard({ data, onSaved }: { data: AiLimits; onSaved: (next: AiLimits) => void }) {
  const [rows, setRows] = useState<LimitRow[]>(() => data.limits.map(toRow))
  const [adding, setAdding] = useState('')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const taken = new Set(rows.map(rowKey))

  const add = () => {
    const [appliesTo, id] = adding.split(':') as [AiLimitAppliesTo, string]
    if (!appliesTo) return

    const name =
      appliesTo === 'role'
        ? (data.roles.find((r) => r.id === id)?.name ?? null)
        : appliesTo === 'user'
          ? (data.users.find((u) => u.id === id)?.name ?? null)
          : null

    setRows((all) => [
      ...all,
      {
        appliesTo,
        roleId: appliesTo === 'role' ? id : null,
        userId: appliesTo === 'user' ? id : null,
        name,
        perDay: '',
        perMonth: '',
        today: null,
        month: null,
      },
    ])
    setAdding('')
  }

  const save = () => {
    const limits = rows.map((r) => ({
      appliesTo: r.appliesTo,
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
      .setAiLimits(limits)
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(false))
  }

  const label = (r: LimitRow) =>
    r.appliesTo === 'everyone' ? 'Everyone' : r.appliesTo === 'role' ? `Role · ${r.name ?? ''}` : `User · ${r.name ?? ''}`

  return (
    <SettingsCard
      title="Spend limits"
      span={12}
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
      {rows.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="text-left text-muted-foreground">
              <tr>
                <th className="py-1 pr-3 font-normal">Applies to</th>
                <th className="py-1 pr-3 font-normal">Per day (USD)</th>
                <th className="py-1 pr-3 font-normal">Per month (USD)</th>
                <th className="py-1 pr-3 font-normal">Spent today</th>
                <th className="py-1 pr-3 font-normal">Spent this month</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={rowKey(r)}>
                  <td className="py-1 pr-3 whitespace-nowrap">{label(r)}</td>
                  <td className="py-1 pr-3">
                    <Money
                      label={`${label(r)} per day`}
                      value={r.perDay}
                      onChange={(v) => setRows((all) => all.map((x, j) => (j === i ? { ...x, perDay: v } : x)))}
                    />
                  </td>
                  <td className="py-1 pr-3">
                    <Money
                      label={`${label(r)} per month`}
                      value={r.perMonth}
                      onChange={(v) => setRows((all) => all.map((x, j) => (j === i ? { ...x, perMonth: v } : x)))}
                    />
                  </td>
                  <td className="py-1 pr-3 whitespace-nowrap tabular-nums" title={tokensTitle(r.today)}>
                    {r.appliesTo === 'role' ? 'Each member' : spentText(r.today)}
                  </td>
                  <td className="py-1 pr-3 whitespace-nowrap tabular-nums" title={tokensTitle(r.month)}>
                    {r.appliesTo === 'role' ? 'Each member' : spentText(r.month)}
                  </td>
                  <td className="py-1 text-right">
                    <Button size="sm" variant="ghost" onClick={() => setRows((all) => all.filter((_, j) => j !== i))}>
                      Remove
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <select
          aria-label="Add a limit for"
          value={adding}
          onChange={(e) => setAdding(e.target.value)}
          className={selectClass}
        >
          <option value="">Add a limit for…</option>
          {!taken.has('everyone:') && <option value="everyone:">Everyone</option>}
          <optgroup label="Roles">
            {data.roles
              .filter((r) => !taken.has(`role:${r.id}`))
              .map((r) => (
                <option key={r.id} value={`role:${r.id}`}>
                  {r.name}
                </option>
              ))}
          </optgroup>
          <optgroup label="Users">
            {data.users
              .filter((u) => !taken.has(`user:${u.id}`))
              .map((u) => (
                <option key={u.id} value={`user:${u.id}`}>
                  {u.name}
                </option>
              ))}
          </optgroup>
        </select>
        <Button size="sm" variant="outline" disabled={!adding} onClick={add}>
          Add
        </Button>
      </div>
    </SettingsCard>
  )
}

type PriceRow = { model: string; input: string; cached: string; output: string }

function PricesCard({ data, onSaved }: { data: AiLimits; onSaved: (next: AiLimits) => void }) {
  const [rows, setRows] = useState<PriceRow[]>(() =>
    data.prices.map((p) => ({
      model: p.model,
      input: String(p.inputPerMillion),
      cached: p.cachedInputPerMillion === null ? '' : String(p.cachedInputPerMillion),
      output: String(p.outputPerMillion),
    })),
  )
  const [adding, setAdding] = useState('')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const listId = useId()

  const set = (i: number, patch: Partial<PriceRow>) =>
    setRows((all) => all.map((x, j) => (j === i ? { ...x, ...patch } : x)))

  const add = () => {
    const model = adding.trim()
    if (!model || rows.some((r) => r.model === model)) return
    setRows((all) => [...all, { model, input: '', cached: '', output: '' }])
    setAdding('')
  }

  const save = () => {
    const prices: AiPrice[] = rows.map((r) => ({
      model: r.model,
      inputPerMillion: amount(r.input) ?? 0,
      cachedInputPerMillion: amount(r.cached),
      outputPerMillion: amount(r.output) ?? 0,
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

  return (
    <SettingsCard
      title="Prices per million tokens"
      span={12}
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
      {rows.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="text-left text-muted-foreground">
              <tr>
                <th className="py-1 pr-3 font-normal">Model</th>
                <th className="py-1 pr-3 font-normal">Input (USD)</th>
                <th className="py-1 pr-3 font-normal">Cached input (USD)</th>
                <th className="py-1 pr-3 font-normal">Output (USD)</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={r.model}>
                  <td className="py-1 pr-3 font-mono whitespace-nowrap">{r.model}</td>
                  <td className="py-1 pr-3">
                    <Money label={`${r.model} input`} value={r.input} onChange={(v) => set(i, { input: v })} />
                  </td>
                  <td className="py-1 pr-3">
                    <Money label={`${r.model} cached input`} value={r.cached} onChange={(v) => set(i, { cached: v })} />
                  </td>
                  <td className="py-1 pr-3">
                    <Money label={`${r.model} output`} value={r.output} onChange={(v) => set(i, { output: v })} />
                  </td>
                  <td className="py-1 text-right">
                    <Button size="sm" variant="ghost" onClick={() => setRows((all) => all.filter((_, j) => j !== i))}>
                      Remove
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
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

const selectClass = cn(
  'border-input focus-visible:border-ring focus-visible:ring-ring/50 dark:bg-input/30',
  'h-9 rounded-md border bg-transparent px-3 text-sm shadow-xs outline-none focus-visible:ring-[3px]',
)

function Money({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <Input
      aria-label={label}
      type="number"
      inputMode="decimal"
      min={0}
      step="any"
      value={value}
      className="w-28"
      onChange={(e) => onChange(e.target.value)}
    />
  )
}
