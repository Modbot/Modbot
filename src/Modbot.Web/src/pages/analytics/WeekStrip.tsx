import { compactNumber } from '@/components/charts'
import type { WeekPair } from '@/lib/api'
import { weekChange } from '@/lib/serverOverview'
import { Section, Stat, StatStrip } from './shared'

/**
 * "This week" on the VRChat and Discord pages: four numbers for the last seven days, each against
 * the seven before, as Discord's Server Insights opens. The same whatever range Stats is on, and
 * the only numbers either page keeps once its charts moved to Stats (Stats page design). The
 * heading opens Stats for someone who may open it.
 */
export function WeekStrip({ href, children }: { href?: string; children: React.ReactNode }) {
  return (
    <Section title="This week" href={href}>
      <div data-week>
        <StatStrip className="sm:grid-cols-4 xl:grid-cols-4">{children}</StatStrip>
      </div>
    </Section>
  )
}

/**
 * One number of the week with an arrow and how far it moved. Green is the good way: up for most
 * numbers, down for one that counts people leaving (`upIsGood` false).
 */
export function WeekStat({
  label,
  pair,
  format = compactNumber,
  upIsGood = true,
}: {
  label: string
  pair: WeekPair
  format?: (n: number) => string
  upIsGood?: boolean
}) {
  const change = weekChange(pair)
  const good = change.way === 'up' ? upIsGood : !upIsGood

  return (
    <Stat
      label={label}
      value={format(pair.thisWeek)}
      note={
        change.way === 'same' ? (
          'Same as last week'
        ) : (
          <span>
            <span className={good ? 'text-ok' : 'text-destructive'}>
              <span aria-hidden>{change.way === 'up' ? '▲' : '▼'} </span>
              <span className="sr-only">{change.way === 'up' ? 'Up' : 'Down'} </span>
              <span className="font-mono">{format(change.by)}</span>
            </span>{' '}
            from last week
          </span>
        )
      }
    />
  )
}
