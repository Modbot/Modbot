import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, ApiError, type AiChatSettings as Settings } from '@/lib/api'
import { LongField, Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { ModelField } from './ModelField'

/**
 * Settings → AI → Chat: whether the Chat page answers, with which model, what it is told, how far
 * one reply may go, and which tools it may use (AI chat design §5).
 */
export function AiChatSettings() {
  const [data, setData] = useState<Settings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .aiChatSettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change AI settings.'
              : 'Could not load Chat settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="ai-chat" title="AI chat settings">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <ChatForm settings={data} onSaved={setData} />
      )}
    </SettingsSection>
  )
}

function ChatForm({ settings, onSaved }: { settings: Settings; onSaved: (next: Settings) => void }) {
  const [enabled, setEnabled] = useState(settings.enabled)
  const [model, setModel] = useState(settings.model ?? '')
  const [instructions, setInstructions] = useState(settings.instructions ?? '')
  const [maxToolCalls, setMaxToolCalls] = useState(String(settings.maxToolCalls))
  const [maxReplyTokens, setMaxReplyTokens] = useState(String(settings.maxReplyTokens))
  const [timeLimit, setTimeLimit] = useState(String(settings.timeLimitSeconds))
  const [tools, setTools] = useState<Record<string, boolean>>(
    Object.fromEntries(settings.tools.map((t) => [t.name, t.enabled])),
  )

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setAiChatSettings({
        enabled,
        model: model.trim() || null,
        instructions: instructions.trim() || null,
        maxToolCalls: Number(maxToolCalls),
        maxReplyTokens: Number(maxReplyTokens),
        timeLimitSeconds: Number(timeLimit),
        tools,
      })
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) =>
        setProblem(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to change AI settings.'
            : e instanceof ApiError
              ? e.message
              : 'Could not reach the Modbot server.',
        ),
      )
      .finally(() => setBusy(false))
  }

  const footer = (
    <>
      <Button size="sm" disabled={busy} onClick={save}>
        {busy ? 'Saving…' : 'Save'}
      </Button>
      <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
      <Outcome tone="problem">{problem}</Outcome>
    </>
  )

  return (
    <>
      <SettingsCard title="Chat" footer={footer}>
        <Switch checked={enabled} onChange={setEnabled}>
          Chat on
        </Switch>
        {!settings.aiEnabled && <Outcome tone="problem">AI is off on Base.</Outcome>}

        <div className="flex max-w-lg flex-col gap-3">
          <ModelField feature="chat" value={model} placeholder={settings.baseModel ?? ''} onChange={setModel} />
          <LongField
            label="Extra instructions"
            value={instructions}
            placeholder=""
            rows={4}
            onChange={setInstructions}
          />
        </div>
      </SettingsCard>

      <SettingsCard title="Limits" footer={footer}>
        <div className="flex max-w-lg flex-col gap-3">
          <NumberField label="Tool calls per reply" value={maxToolCalls} min={0} max={50} onChange={setMaxToolCalls} />
          <NumberField label="Reply length (tokens)" value={maxReplyTokens} min={256} max={32000} onChange={setMaxReplyTokens} />
          <NumberField label="Time limit (seconds)" value={timeLimit} min={10} max={600} onChange={setTimeLimit} />
        </div>
      </SettingsCard>

      <SettingsCard title="Tools" span={12} footer={footer}>
        <ul className="grid gap-x-6 gap-y-2.5 md:grid-cols-2">
          {settings.tools.map((t) => (
            <li key={t.name} className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <Switch checked={tools[t.name] ?? false} onChange={(on) => setTools((all) => ({ ...all, [t.name]: on }))}>
                {t.label}
              </Switch>
              <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {[t.onlyReads ? 'Reads' : 'Acts', ...t.needs].join(' · ')}
              </span>
            </li>
          ))}
        </ul>
      </SettingsCard>
    </>
  )
}

function NumberField({
  label,
  value,
  min,
  max,
  onChange,
}: {
  label: string
  value: string
  min: number
  max: number
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input type="number" inputMode="numeric" min={min} max={max} value={value} onChange={(e) => onChange(e.target.value)} />
    </label>
  )
}
