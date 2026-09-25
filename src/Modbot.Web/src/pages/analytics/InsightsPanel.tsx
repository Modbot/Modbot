import { useEffect, useMemo, useState } from 'react'
import { InsightBody } from '@/components/insights/InsightBody'
import { Select } from '@/components/ui/select'
import { insightDays } from '@/components/insights/days'
import { api, type Insight, type InsightKind } from '@/lib/api'
import { Panel, Toggle } from './shared'

/**
 * The AI-written summaries on My Group: the latest of each kind, and the earlier ones behind a
 * picker (AI insights design §4).
 *
 * Renders nothing when there are none -- which is every deployment that has not switched insights
 * on -- rather than an empty panel asking to be set up.
 */
export function InsightsPanel() {
  const [insights, setInsights] = useState<Insight[] | null>(null)
  const [kind, setKind] = useState<InsightKind | null>(null)
  const [chosen, setChosen] = useState<string | null>(null)

  useEffect(() => {
    api
      .insights(undefined, 50)
      .then((r) => setInsights(r.insights))
      .catch(() => setInsights([]))
  }, [])

  const kinds = useMemo(() => {
    const seen = new Map<InsightKind, string>()
    for (const i of insights ?? []) if (!seen.has(i.kind)) seen.set(i.kind, i.label)
    return [...seen].map(([value, label]) => ({ value, label }))
  }, [insights])

  if (!insights || kinds.length === 0) return null

  const shown = kind && kinds.some((k) => k.value === kind) ? kind : kinds[0].value
  const ofKind = insights.filter((i) => i.kind === shown)
  const insight = ofKind.find((i) => i.id === chosen) ?? ofKind[0]

  return (
    <Panel
      title="Insights"
      right={
        <span className="flex flex-wrap items-center gap-2">
          {kinds.length > 1 && (
            <Toggle
              value={shown}
              onChange={(k) => {
                setKind(k)
                setChosen(null)
              }}
              options={kinds}
            />
          )}
          <Select size="sm" aria-label="Days" value={insight.id} onChange={setChosen}>
            {ofKind.map((i) => (
              <option key={i.id} value={i.id}>
                {insightDays(i)}
              </option>
            ))}
          </Select>
        </span>
      }
    >
      <InsightBody insight={insight} />
    </Panel>
  )
}
