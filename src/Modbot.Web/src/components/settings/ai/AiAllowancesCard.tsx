import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { count, money, share, spentText } from '@/lib/aiSpend'
import { api, ApiError, type AiAllowanceAmount, type AiLimits, type AiMemberAllowance } from '@/lib/api'
import { Outcome } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** A number box's text as a number, or null when empty. NaN when it is not a number. */
function amount(text: string): number | null {
  return text.trim() === '' ? null : Number(text)
}

const text = (n: number | null) => (n === null ? '' : String(n))

/** "45% of tokens", or "Over (tokens)" for an allowance of zero that has been used. */
const of = (part: string, what: string) => (part === 'over' ? `Over (${what})` : `${part} of ${what}`)

const failure = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'You do not have permission to change AI settings.'
    : e instanceof ApiError
      ? e.message
      : 'Could not reach the Modbot server.'

/** One member as the card holds it while somebody edits: their own amounts, or the default's. */
type Row = { userId: string; name: string; own: boolean; tokens: string; money: string }

const rowOf = (m: AiMemberAllowance): Row => ({
  userId: m.userId,
  name: m.name,
  own: m.own !== null,
  tokens: text(m.own?.tokens ?? null),
  money: text(m.own?.money ?? null),
})

/**
 * Settings → AI → Limits → Monthly AI allowance: how much AI each team member may use in a month, in
 * tokens and in US dollars, and how much each has used so far.
 *
 * One default for everyone, and a member can be given an amount of their own instead. Tokens work
 * for every model; dollars are counted only where the model has a price, so a model with no price is
 * stopped by tokens alone. Use that no account is behind -- insights on a schedule, AutoMod's own
 * checks -- is in nobody's figure.
 */
export function AiAllowancesCard({ data, onSaved }: { data: AiLimits; onSaved: (next: AiLimits) => void }) {
  const { allowances } = data

  const [defaultTokens, setDefaultTokens] = useState(() => text(allowances.default.tokens))
  const [defaultMoney, setDefaultMoney] = useState(() => text(allowances.default.money))
  const [rows, setRows] = useState<Row[]>(() => allowances.members.map(rowOf))
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const set = (i: number, patch: Partial<Row>) =>
    setRows((all) => all.map((r, j) => (j === i ? { ...r, ...patch } : r)))

  const save = () => {
    const tokens = amount(defaultTokens)
    const dollars = amount(defaultMoney)
    const members = rows.filter((r) => r.own).map((r) => ({ userId: r.userId, tokens: amount(r.tokens), money: amount(r.money) }))

    const numbers = [tokens, dollars, ...members.flatMap((m) => [m.tokens, m.money])]
    if (numbers.some((n) => n !== null && (Number.isNaN(n) || n < 0))) {
      setProblem('An allowance must be a number, zero or more.')
      return
    }

    if ([tokens, ...members.map((m) => m.tokens)].some((n) => n !== null && !Number.isInteger(n))) {
      setProblem('An allowance in tokens must be a whole number.')
      return
    }

    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setAiAllowances({ default: { tokens, money: dollars }, members })
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(false))
  }

  // What a row's allowance is right now, as typed: the default's while the row uses the default.
  const shown = (r: Row): AiAllowanceAmount =>
    r.own
      ? { tokens: amount(r.tokens), money: amount(r.money) }
      : { tokens: amount(defaultTokens), money: amount(defaultMoney) }

  const numeric = (n: number | null): n is number => n !== null && !Number.isNaN(n)

  return (
    <SettingsCard
      title="Monthly AI allowance per team member"
      span={12}
      flush
      footer={
        <>
          <Button size="xs" disabled={busy} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Table
        pinFirst
        head={
          <>
            <Th>Team member</Th>
            <Th>Allowance</Th>
            <Th>Tokens per month</Th>
            <Th>US dollars per month</Th>
            <Th className="text-right">Used this month</Th>
            <Th className="text-right">Of the allowance</Th>
          </>
        }
      >
        <Tr>
          <Td className="font-medium">Everyone</Td>
          <Td className="text-muted-foreground">Default</Td>
          <Td>
            <Amount
              label="Default tokens per month"
              unit="tokens"
              value={defaultTokens}
              step="1"
              onChange={setDefaultTokens}
            />
          </Td>
          <Td>
            <Amount
              label="Default US dollars per month"
              unit="USD"
              value={defaultMoney}
              step="any"
              onChange={setDefaultMoney}
            />
          </Td>
          <Td className="text-right text-muted-foreground">—</Td>
          <Td className="text-right text-muted-foreground">—</Td>
        </Tr>
        {rows.map((r, i) => {
          const member = allowances.members[i]
          const now = shown(r)
          const tokensShare = numeric(now.tokens) ? share(member.month.inputTokens + member.month.outputTokens, now.tokens) : ''
          const moneyShare = numeric(now.money) && member.month.unpricedTokens === 0 ? share(member.month.cost, now.money) : ''

          return (
            <Tr key={r.userId}>
              <Td>{r.name}</Td>
              <Td>
                <Select
                  size="sm"
                  aria-label={`${r.name} allowance`}
                  value={r.own ? 'own' : 'default'}
                  onChange={(v) => set(i, { own: v === 'own' })}
                >
                  <option value="default">Default</option>
                  <option value="own">Own amount</option>
                </Select>
              </Td>
              <Td>
                {r.own ? (
                  <Amount
                    label={`${r.name} tokens per month`}
                    unit="tokens"
                    value={r.tokens}
                    step="1"
                    placeholder="No limit"
                    onChange={(v) => set(i, { tokens: v })}
                  />
                ) : (
                  <Fixed value={now.tokens === null ? 'No limit' : `${count(now.tokens)} tokens`} />
                )}
              </Td>
              <Td>
                {r.own ? (
                  <Amount
                    label={`${r.name} US dollars per month`}
                    unit="USD"
                    value={r.money}
                    step="any"
                    placeholder="No limit"
                    onChange={(v) => set(i, { money: v })}
                  />
                ) : (
                  <Fixed value={now.money === null ? 'No limit' : `${money(now.money)}`} />
                )}
              </Td>
              <Td className="text-right">
                <div className="font-mono whitespace-nowrap">
                  <div>{count(member.month.inputTokens + member.month.outputTokens)} tokens</div>
                  <div className="text-muted-foreground">{spentText(member.month)}</div>
                </div>
              </Td>
              <Td className="text-right font-mono whitespace-nowrap">
                {member.pastLimits ? (
                  <span className="text-muted-foreground">Not limited</span>
                ) : (
                  <>
                    <div>{tokensShare ? of(tokensShare, 'tokens') : ''}</div>
                    <div className="text-muted-foreground">{moneyShare ? of(moneyShare, 'dollars') : ''}</div>
                    {!tokensShare && !moneyShare && <span className="text-muted-foreground">—</span>}
                  </>
                )}
              </Td>
            </Tr>
          )
        })}
      </Table>
    </SettingsCard>
  )
}

/** A number box with its unit written beside it, so no figure stands alone. */
function Amount({
  label,
  unit,
  value,
  step,
  placeholder,
  onChange,
}: {
  label: string
  unit: string
  value: string
  step: string
  placeholder?: string
  onChange: (v: string) => void
}) {
  return (
    <span className="flex items-center gap-1.5 whitespace-nowrap">
      <Input
        aria-label={label}
        type="number"
        inputMode={step === 'any' ? 'decimal' : 'numeric'}
        min={0}
        step={step}
        value={value}
        placeholder={placeholder ?? 'No limit'}
        className="w-32"
        onChange={(e) => onChange(e.target.value)}
      />
      <span className="text-muted-foreground">{unit}</span>
    </span>
  )
}

function Fixed({ value }: { value: string }) {
  return <span className="font-mono whitespace-nowrap text-muted-foreground">{value}</span>
}
