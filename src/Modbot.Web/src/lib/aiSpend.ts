import type { AiSpendWarning, AiSpent } from '@/lib/api'

/**
 * How AI spend is written, on the limits page and on Health.
 *
 * Spend of a model with no price is unknown, never zero (AI chat design §10): a figure with any
 * unpriced tokens says so beside the money, and one with nothing but unpriced tokens is "Unknown".
 */

const usd = new Intl.NumberFormat(undefined, {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
})

const whole = new Intl.NumberFormat()

export const money = (n: number): string => usd.format(n)

export const count = (n: number): string => whole.format(n)

export function spentText(s: AiSpent | null | undefined): string {
  if (!s) return '—'
  if (s.unpricedTokens > 0) return s.cost === 0 ? 'Unknown' : `${money(s.cost)} + unknown`
  return money(s.cost)
}

export const tokensText = (s: AiSpent | null | undefined): string =>
  s ? `${count(s.inputTokens + s.outputTokens)} tokens` : ''

export function tokensTitle(s: AiSpent | null | undefined): string | undefined {
  if (!s) return undefined
  const parts = [`${count(s.inputTokens)} in (${count(s.cachedInputTokens)} cached)`, `${count(s.outputTokens)} out`]
  if (s.unpricedTokens > 0) parts.push(`${count(s.unpricedTokens)} with no price`)
  return parts.join(' · ')
}

/** "45%", or nothing when there is no limit to be a share of. */
export function share(part: number, limit: number | null | undefined): string {
  if (limit === null || limit === undefined) return ''
  if (limit <= 0) return part > 0 ? 'over' : '100%'
  return `${Math.round((part / limit) * 100)}%`
}

/** An amount in a limit's own unit. */
export const amountText = (n: number, unit: AiSpendWarning['unit']): string =>
  unit === 'tokens' ? `${count(n)} tokens` : money(n)
