import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ago } from '@/lib/format'
import {
  actionLabel,
  failure,
  moderationApi,
  languageStatsLabel,
  scopeLabel,
  seesLabel,
  statsLabel,
  targetLabel,
  TARGETS,
  testsLabel,
  trialLabel,
  type AiToolView,
  type AutoMod,
  type ModerationTarget,
  type RuleKind,
  type RuleSafety,
  type TermListView,
  type TopicView,
  type TryResult,
} from '@/lib/autoMod'
import { cn } from '@/lib/utils'
import { Checkbox, LongField, Outcome, Placeholder, Row, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { HubListsDialog } from './HubListsDialog'
import { TermListDialog } from './TermListDialog'
import { TestSetDialog } from './TestSetDialog'
import { TopicDialog } from './TopicDialog'

/**
 * Settings → AutoMod: the switch, term lists (local and from Modbot Hub), the "Try it" box, and --
 * only while AI is on under Settings → AI → Base -- the AI topics and the AI tools (AutoMod design).
 *
 * The AI section reads the same switch the Base card saves, carried on the response as `aiEnabled`,
 * so switching AI off there hides everything here that would call it.
 */
export function AutoModSection() {
  const [data, setData] = useState<AutoMod | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      moderationApi
        .settings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) => setError(failure(e, 'Could not load AutoMod settings.'))),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="automod" title="AutoMod">
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <SwitchCard settings={data} onSaved={setData} />
          <TryCard aiEnabled={data.aiEnabled} />
          <TermListsCard
            lists={data.lists}
            picturesAvailable={data.picturesAvailable}
            onChanged={() => void load()}
          />
          {data.aiEnabled && (
            <>
              <TopicsCard
                topics={data.topics}
                aiReady={data.aiReady}
                picturesAvailable={data.picturesAvailable}
                onChanged={() => void load()}
              />
              <AiToolsCard settings={data} onSaved={setData} />
            </>
          )}
        </>
      )}
    </SettingsSection>
  )
}

function SwitchCard({ settings, onSaved }: { settings: AutoMod; onSaved: (next: AutoMod) => void }) {
  const [enabled, setEnabled] = useState(settings.enabled)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    moderationApi
      .saveSettings({ enabled })
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not save.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="AutoMod"
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
      <Switch checked={enabled} onChange={setEnabled}>
        AutoMod on
      </Switch>
    </SettingsCard>
  )
}

/**
 * The AI tools AutoMod may use, the daily AI call limit and today's count. Shown only while AI is
 * on, because none of it does anything otherwise.
 */
function AiToolsCard({ settings, onSaved }: { settings: AutoMod; onSaved: (next: AutoMod) => void }) {
  const [tools, setTools] = useState<Record<string, boolean>>(
    Object.fromEntries(settings.aiTools.map((t) => [t.name, t.on])),
  )
  const [limit, setLimit] = useState(String(settings.dailyAiCallLimit))
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    moderationApi
      .saveSettings({
        enabled: settings.enabled,
        dailyAiCallLimit: Number.parseInt(limit, 10) || 0,
        aiTools: tools,
      })
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not save.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="AI tools"
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
      <ul className="grid gap-x-6 gap-y-2.5 md:grid-cols-2">
        {settings.aiTools.map((t: AiToolView) => (
          <li key={t.name} className="flex items-center">
            <Switch checked={tools[t.name] ?? false} onChange={(on) => setTools((all) => ({ ...all, [t.name]: on }))}>
              {t.label}
            </Switch>
          </li>
        ))}
      </ul>
      <label className="flex max-w-xs flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Daily AI call limit</span>
        <Input type="number" min={0} max={100000} value={limit} onChange={(e) => setLimit(e.target.value)} />
      </label>
      <div className="flex max-w-xs flex-col">
        <Row label="AI calls today" value={`${settings.aiCallsToday} of ${settings.dailyAiCallLimit}`} />
        <Row label="AI" value={settings.aiReady ? 'Ready' : 'Off'} />
      </div>
    </SettingsCard>
  )
}

/** One rule's line: its name and state on the left, its controls on the right. */
function RuleRow({
  name,
  badges,
  details,
  enabled,
  busy,
  onToggle,
  children,
}: {
  name: string
  badges?: React.ReactNode
  details: (string | null)[]
  enabled: boolean
  busy: boolean
  onToggle: (on: boolean) => void
  children: React.ReactNode
}) {
  return (
    <li
      className={cn(
        'flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b py-2 last:border-0',
        !enabled && 'text-muted-foreground',
      )}
      style={{
        borderBottomWidth: 'var(--hairline)',
        fontSize: 'var(--text-small)',
      }}
    >
      <Switch checked={enabled} disabled={busy} onChange={onToggle}>
        <span className="sr-only">On</span>
      </Switch>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{name}</span>
          {badges}
        </div>
        <div className="text-muted-foreground">{details.filter(Boolean).join(' · ')}</div>
      </div>
      <div className="flex flex-wrap items-center gap-1">{children}</div>
    </li>
  )
}

/** The trial, a pause and the test set, shown the same way on both kinds of rule. */
function SafetyBadges({ rule }: { rule: RuleSafety }) {
  return (
    <>
      {rule.paused && <Badge variant="destructive">Paused</Badge>}
      {rule.trial && <Badge variant="secondary">Trial</Badge>}
      {rule.acting && <Badge variant="destructive">Acting</Badge>}
    </>
  )
}

function safetyDetails(rule: RuleSafety): (string | null)[] {
  return [
    rule.paused?.reason ?? null,
    rule.trial ? trialLabel(rule.trial) : null,
    testsLabel(rule.tests),
    scopeLabel(rule.scope),
    seesLabel(rule),
  ]
}

/** End trial and Resume: the two buttons only a person may press (design §13). */
function SafetyButtons({
  kind,
  rule,
  busy,
  onTests,
  onRun,
}: {
  kind: RuleKind
  rule: RuleSafety & { id: string }
  busy: boolean
  onTests: () => void
  onRun: (work: () => Promise<unknown>) => void
}) {
  return (
    <>
      {rule.paused && (
        <Button size="xs" disabled={busy} onClick={() => onRun(() => moderationApi.resume(kind, rule.id))}>
          Resume
        </Button>
      )}
      {rule.trial && (
        <Button size="xs" disabled={busy} onClick={() => onRun(() => moderationApi.endTrial(kind, rule.id))}>
          End trial
        </Button>
      )}
      <Button size="xs" variant="ghost" disabled={busy} onClick={onTests}>
        Test set
      </Button>
    </>
  )
}

function acts(rule: { deleteMessage: boolean; timeoutMinutes: number | null; groupBan: boolean; groupRemove: boolean }) {
  return rule.deleteMessage || rule.timeoutMinutes !== null || rule.groupBan || rule.groupRemove
}

function TermListsCard({
  lists,
  picturesAvailable,
  onChanged,
}: {
  lists: TermListView[]
  picturesAvailable: boolean
  onChanged: () => void
}) {
  const [editing, setEditing] = useState<{ id: string | null } | null>(null)
  const [testing, setTesting] = useState<{ kind: RuleKind; id: string; name: string } | null>(null)
  const [hubOpen, setHubOpen] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const run = (id: string, work: () => Promise<unknown>) => {
    setBusy(id)
    setProblem(null)
    work()
      .then(onChanged)
      .catch((e: unknown) => setProblem(failure(e, 'Could not save the change.')))
      .finally(() => setBusy(null))
  }

  const toggle = (list: TermListView, enabled: boolean) =>
    run(list.id, () =>
      moderationApi.updateList(list.id, {
        name: list.name,
        enabled,
        targets: list.targets,
        deleteMessage: list.deleteMessage,
        timeoutMinutes: list.timeoutMinutes,
        groupBan: list.groupBan,
        groupRemove: list.groupRemove,
        scope: list.scope,
        trialDays: null,
        contextMessages: list.contextMessages,
        checkPictures: list.checkPictures,
        openReviewForEachFlag: list.openReviewForEachFlag,
      }),
    )

  const now = new Date().toISOString()

  return (
    <SettingsCard
      span={12}
      title="Term lists"
      action={
        <div className="flex gap-1">
          <Button size="xs" variant="outline" onClick={() => setHubOpen(true)}>
            Add from Modbot Hub
          </Button>
          <Button size="xs" variant="outline" onClick={() => setEditing({ id: null })}>
            New list
          </Button>
        </div>
      }
      footer={problem ? <Outcome tone="problem">{problem}</Outcome> : undefined}
    >
      {lists.length === 0 ? (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          No term lists
        </span>
      ) : (
        <ul className="flex flex-col">
          {lists.map((list) => (
            <RuleRow
              key={list.id}
              name={list.name}
              enabled={list.enabled}
              busy={busy !== null}
              onToggle={(on) => toggle(list, on)}
              badges={
                <>
                  <Badge variant="outline">{list.source === 'cloud' ? 'Modbot Hub' : 'Local'}</Badge>
                  {list.source === 'cloud' && list.hubVersion && (
                    <Badge variant="secondary">{list.hubVersion}</Badge>
                  )}
                  {acts(list) && (
                    <Badge
                      variant="destructive"
                      title={list.setToActBy ? `Set by ${list.setToActBy}` : undefined}
                    >
                      {actionLabel(list)}
                    </Badge>
                  )}
                  <SafetyBadges rule={list} />
                </>
              }
              details={[
                `${list.termCount - list.excludedCount} terms`,
                list.targets.map((t) => targetLabel(t)).join(', '),
                statsLabel(list.stats),
                languageStatsLabel(list.stats),
                ...safetyDetails(list),
                list.source === 'cloud' ? `Fetched ${ago(list.hubFetchedAt, now)}` : null,
                list.hubError,
              ]}
            >
              <SafetyButtons
                kind="termList"
                rule={list}
                busy={busy !== null}
                onTests={() => setTesting({ kind: 'termList', id: list.id, name: list.name })}
                onRun={(work) => run(list.id, work)}
              />
              {list.hubAvailableVersion && (
                <Button
                  size="xs"
                  disabled={busy !== null}
                  title={
                    list.hubAvailableChanges
                      ? `+${list.hubAvailableChanges.added} −${list.hubAvailableChanges.removed} ~${list.hubAvailableChanges.changed}`
                      : undefined
                  }
                  onClick={() => run(list.id, () => moderationApi.applyHubUpdate(list.id))}
                >
                  Update to {list.hubAvailableVersion}
                </Button>
              )}
              {list.source === 'cloud' && (
                <Button
                  size="xs"
                  variant="ghost"
                  disabled={busy !== null}
                  onClick={() => run(list.id, () => moderationApi.refreshHubList(list.id))}
                >
                  {busy === list.id ? 'Refreshing…' : 'Refresh'}
                </Button>
              )}
              <Button
                size="xs"
                variant="ghost"
                disabled={busy !== null}
                onClick={() => setEditing({ id: list.id })}
              >
                Edit
              </Button>
              <Button
                size="xs"
                variant="ghost"
                disabled={busy !== null}
                onClick={() => {
                  if (window.confirm(`Delete ${list.name}?`))
                    run(list.id, () => moderationApi.deleteList(list.id))
                }}
              >
                Delete
              </Button>
            </RuleRow>
          ))}
        </ul>
      )}

      <TermListDialog
        listId={editing?.id ?? null}
        open={editing !== null}
        picturesAvailable={picturesAvailable}
        onClose={() => setEditing(null)}
        onSaved={onChanged}
      />
      <HubListsDialog open={hubOpen} onClose={() => setHubOpen(false)} onAdded={onChanged} />
      <TestSetDialog rule={testing} open={testing !== null} onClose={() => setTesting(null)} onRan={onChanged} />
    </SettingsCard>
  )
}

function TopicsCard({
  topics,
  aiReady,
  picturesAvailable,
  onChanged,
}: {
  topics: TopicView[]
  aiReady: boolean
  picturesAvailable: boolean
  onChanged: () => void
}) {
  const [editing, setEditing] = useState<{ topic: TopicView | null } | null>(null)
  const [testing, setTesting] = useState<{ kind: RuleKind; id: string; name: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const run = (work: () => Promise<unknown>) => {
    setBusy(true)
    setProblem(null)
    work()
      .then(onChanged)
      .catch((e: unknown) => setProblem(failure(e, 'Could not save the change.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      span={12}
      title="AI topics"
      action={
        <div className="flex items-center gap-2">
          {!aiReady && <Badge variant="outline">AI off</Badge>}
          <Button size="xs" variant="outline" onClick={() => setEditing({ topic: null })}>
            New topic
          </Button>
        </div>
      }
      footer={problem ? <Outcome tone="problem">{problem}</Outcome> : undefined}
    >
      {topics.length === 0 ? (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          No AI topics
        </span>
      ) : (
        <ul className="flex flex-col">
          {topics.map((topic) => (
            <RuleRow
              key={topic.id}
              name={topic.name}
              enabled={topic.enabled}
              busy={busy}
              onToggle={(enabled) => run(() => moderationApi.updateTopic(topic.id, { ...topic, enabled, trialDays: null }))}
              badges={
                <>
                  <Badge variant="outline">Sensitivity {topic.sensitivity}</Badge>
                  {acts(topic) && (
                    <Badge
                      variant="destructive"
                      title={topic.setToActBy ? `Set by ${topic.setToActBy}` : undefined}
                    >
                      {actionLabel(topic)}
                    </Badge>
                  )}
                  <SafetyBadges rule={topic} />
                </>
              }
              details={[
                topic.targets.map((t) => targetLabel(t)).join(', '),
                statsLabel(topic.stats),
                languageStatsLabel(topic.stats),
                ...safetyDetails(topic),
              ]}
            >
              <SafetyButtons
                kind="topic"
                rule={topic}
                busy={busy}
                onTests={() => setTesting({ kind: 'topic', id: topic.id, name: topic.name })}
                onRun={(work) => run(work)}
              />
              <Button size="xs" variant="ghost" disabled={busy} onClick={() => setEditing({ topic })}>
                Edit
              </Button>
              <Button
                size="xs"
                variant="ghost"
                disabled={busy}
                onClick={() => {
                  if (window.confirm(`Delete ${topic.name}?`)) run(() => moderationApi.deleteTopic(topic.id))
                }}
              >
                Delete
              </Button>
            </RuleRow>
          ))}
        </ul>
      )}

      <TopicDialog
        topic={editing?.topic ?? null}
        open={editing !== null}
        picturesAvailable={picturesAvailable}
        onClose={() => setEditing(null)}
        onSaved={onChanged}
      />
      <TestSetDialog rule={testing} open={testing !== null} onClose={() => setTesting(null)} onRan={onChanged} />
    </SettingsCard>
  )
}

function TryCard({ aiEnabled }: { aiEnabled: boolean }) {
  const [text, setText] = useState('')
  const [target, setTarget] = useState<ModerationTarget>('discordMessage')
  const [includeAi, setIncludeAi] = useState(false)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<TryResult | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const run = () => {
    setBusy(true)
    setProblem(null)
    setResult(null)

    moderationApi
      .tryText({ text, target, includeAi: aiEnabled && includeAi })
      .then(setResult)
      .catch((e: unknown) => setProblem(failure(e, 'Could not check the text.')))
      .finally(() => setBusy(false))
  }

  const outcome = result
    ? [
        result.wouldDeleteMessage ? 'Delete the message' : null,
        result.wouldTimeOutMinutes ? `Time out for ${result.wouldTimeOutMinutes} minutes` : null,
        result.wouldGroupBan ? 'Ban from the group' : null,
        result.wouldGroupRemove ? 'Remove from the group' : null,
      ].filter(Boolean)
    : []

  return (
    <SettingsCard
      title="Try it"
      footer={
        <>
          <Button size="sm" variant="outline" disabled={busy || !text.trim()} onClick={run}>
            {busy ? 'Checking…' : 'Try'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <LongField label="Text" value={text} placeholder="" rows={3} onChange={setText} />
      <div className="flex flex-wrap items-center gap-4" style={{ fontSize: 'var(--text-small)' }}>
        <Select
          value={target}
          onChange={(t) => setTarget(t as ModerationTarget)}
          aria-label="Checks"
        >
          {TARGETS.map((t) => (
            <option key={t.value} value={t.value}>
              {t.label}
            </option>
          ))}
        </Select>
        {aiEnabled && (
          <Checkbox checked={includeAi} onChange={setIncludeAi}>
            Include AI topics
          </Checkbox>
        )}
      </div>

      {result && (
        <div className="flex flex-col gap-2" style={{ fontSize: 'var(--text-small)' }}>
          {result.matches.length === 0 ? (
            <span className="text-muted-foreground">Nothing matched</span>
          ) : (
            <ul className="flex flex-col">
              {result.matches.map((m, i) => (
                <li
                  key={`${m.ruleId}-${i}`}
                  className={cn(
                    'flex flex-col border-b py-1.5 last:border-0',
                    !m.ruleEnabled && 'text-muted-foreground',
                  )}
                  style={{ borderBottomWidth: 'var(--hairline)' }}
                >
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{m.ruleName}</span>
                    {!m.ruleEnabled && <Badge variant="outline">Off</Badge>}
                    <Badge variant="secondary">{actionLabel(m)}</Badge>
                  </div>
                  <div>
                    <span className="font-mono">{m.term}</span> → “{m.matched}”
                  </div>
                  {m.reason && <div className="text-muted-foreground">{m.reason}</div>}
                </li>
              ))}
            </ul>
          )}
          {result.matches.length > 0 && (
            <div className="font-medium">{outcome.length > 0 ? outcome.join(' · ') : 'Flag only'}</div>
          )}
          {result.aiSkipped && <Outcome tone="problem">{result.aiSkipped}</Outcome>}
        </div>
      )}
    </SettingsCard>
  )
}
