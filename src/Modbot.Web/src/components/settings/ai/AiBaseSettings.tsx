import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { api, ApiError, type AiSettings, type AiSettingsInput } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Field, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { ModelField } from './ModelField'

/**
 * Settings → AI → Base: where AI requests go, with which key, to which model, and whether they go
 * at all.
 *
 * The key is write-only. The server says whether one is stored and never what it is, and it only
 * ever sends a stored key to the endpoint it was saved with -- so changing the endpoint and saving
 * without typing a key forgets it, and Test against a different endpoint goes without it.
 */
export function AiBaseSettings() {
  const [data, setData] = useState<AiSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .aiSettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change AI settings.'
              : 'Could not load AI settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="ai-base" title="AI base settings">
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <ConnectionCard settings={data} onSaved={setData} />
      )}
    </SettingsSection>
  )
}

function ConnectionCard({
  settings,
  onSaved,
}: {
  settings: AiSettings
  onSaved: (next: AiSettings) => void
}) {
  const presetEndpoint = (id: string) => settings.providers.find((p) => p.id === id)?.endpoint ?? ''

  const [enabled, setEnabled] = useState(settings.enabled)
  const [provider, setProvider] = useState(settings.provider)
  const [endpoint, setEndpoint] = useState(settings.endpoint ?? presetEndpoint(settings.provider))
  const [apiKey, setApiKey] = useState('')
  const [model, setModel] = useState(settings.model ?? '')

  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [test, setTest] = useState<{ worked: boolean; message: string } | null>(null)

  const chooseProvider = (id: string) => {
    setProvider(id)
    setEndpoint(presetEndpoint(id))
  }

  const failure = (e: unknown) =>
    e instanceof ApiError && e.status === 403
      ? 'You do not have permission to change AI settings.'
      : e instanceof ApiError
        ? e.message
        : 'Could not reach the Modbot server.'

  const connection = () => ({
    provider,
    endpoint: endpoint.trim(),
    model: model.trim(),
    ...(apiKey.trim() ? { apiKey: apiKey.trim() } : {}),
  })

  const save = (body: AiSettingsInput, what: 'save' | 'remove') => {
    setBusy(what)
    setSaved(false)
    setProblem(null)
    setTest(null)

    api
      .setAiSettings(body)
      .then((next) => {
        setApiKey('')
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(null))
  }

  const runTest = () => {
    setBusy('test')
    setSaved(false)
    setProblem(null)
    setTest(null)

    api
      .testAi(connection())
      .then(setTest)
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(null))
  }

  const body = (): AiSettingsInput => ({ enabled, ...connection() })

  return (
    <SettingsCard
      title="Connection"
      footer={
        <>
          <Button size="sm" disabled={busy !== null} onClick={() => save(body(), 'save')}>
            {busy === 'save' ? 'Saving…' : 'Save'}
          </Button>
          <Button
            size="sm"
            variant="outline"
            disabled={busy !== null || !endpoint.trim() || !model.trim()}
            onClick={runTest}
          >
            {busy === 'test' ? 'Testing…' : 'Test'}
          </Button>
          {settings.apiKeyStored && (
            <Button
              size="sm"
              variant="outline"
              disabled={busy !== null}
              onClick={() =>
                save({ ...body(), apiKey: undefined, removeApiKey: true }, 'remove')
              }
            >
              {busy === 'remove' ? 'Removing…' : 'Remove key'}
            </Button>
          )}
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
          {test && <Outcome tone={test.worked ? 'ok' : 'problem'}>{test.message}</Outcome>}
        </>
      }
    >
      <Switch checked={enabled} onChange={setEnabled}>
        AI on
      </Switch>

      <div role="group" aria-label="Provider" className="flex flex-wrap gap-1.5">
        {settings.providers.map((p) => (
          <button
            key={p.id}
            type="button"
            aria-pressed={provider === p.id}
            onClick={() => chooseProvider(p.id)}
            className={cn(
              'inline-flex items-center rounded-full border px-2.5 font-medium transition-colors',
              provider === p.id
                ? 'border-transparent bg-accent text-accent-foreground'
                : 'text-muted-foreground hover:text-foreground',
            )}
            style={{
              fontSize: 'var(--text-small)',
              borderWidth: 'var(--hairline)',
              height: 'calc(var(--control-h) - 6px)',
            }}
          >
            {p.label}
            {p.recommended && (
              <Badge variant="secondary" className="ml-1.5">
                Recommended
              </Badge>
            )}
          </button>
        ))}
      </div>

      <div className="flex max-w-lg flex-col gap-3">
        <Field
          label="Endpoint"
          value={endpoint}
          placeholder={presetEndpoint(provider) || 'http://localhost:11434/v1'}
          onChange={setEndpoint}
        />
        <PasswordField
          label={settings.apiKeyStored ? 'API key (stored)' : 'API key'}
          value={apiKey}
          onChange={setApiKey}
        />
        <ModelField
          feature="base"
          value={model}
          provider={provider}
          endpoint={endpoint.trim()}
          apiKey={apiKey.trim()}
          onChange={setModel}
        />
      </div>
    </SettingsCard>
  )
}
