import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { api, ApiError, type AiSettings, type AiSettingsInput } from '@/lib/api'
import { Field, NumberField, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { ModelField, Toggle } from './ModelField'

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
  const [fallbackModel, setFallbackModel] = useState(settings.fallbackModel ?? '')
  const [keepDays, setKeepDays] = useState(String(settings.callLogKeepDays))

  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)
  const [confirming, setConfirming] = useState(false)
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

  const body = (): AiSettingsInput => ({
    enabled,
    ...connection(),
    fallbackModel: fallbackModel.trim(),
    callLogKeepDays: Number(keepDays) || 0,
  })

  // M8 §4.5: the first time AI is switched on, the operator reads what is sent where and confirms
  // it. Once for the deployment, so a confirmed deployment never sees this again.
  const saveOrConfirm = () => {
    if (enabled && !settings.acknowledgement.confirmed) {
      setConfirming(true)
      return
    }

    save(body(), 'save')
  }

  const confirm = () => {
    setBusy('save')
    setProblem(null)

    api
      .acknowledgeAi(endpoint.trim())
      .then((next) => {
        onSaved(next)
        setConfirming(false)
        return api.setAiSettings(body())
      })
      .then((next) => {
        setApiKey('')
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(null))
  }

  return (
    <SettingsCard
      title="Connection"
      footer={
        <>
          <Button size="sm" disabled={busy !== null} onClick={saveOrConfirm}>
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

      <Dialog open={confirming} onOpenChange={(next) => !next && setConfirming(false)}>
        {confirming && (
          <DialogContent title="What is sent to the provider" className="max-w-[640px]">
            <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
              <div>
                <span className="text-muted-foreground">Sent to</span>{' '}
                <span className="font-mono">{endpoint.trim() || settings.acknowledgement.endpoint}</span>
              </div>
              <ul className="flex flex-col gap-2">
                {settings.acknowledgement.sends.map((s) => (
                  <li key={s.feature}>
                    <span className="font-medium">{s.feature}</span>
                    <div className="text-muted-foreground">{s.text}</div>
                  </li>
                ))}
              </ul>
              <div className="flex items-center gap-2">
                <Button size="sm" disabled={busy !== null} onClick={confirm}>
                  {busy === 'save' ? 'Saving…' : 'I understand'}
                </Button>
                <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => setConfirming(false)}>
                  Cancel
                </Button>
              </div>
            </div>
          </DialogContent>
        )}
      </Dialog>

      <div role="group" aria-label="Provider" className="flex flex-wrap gap-1.5">
        {settings.providers.map((p) => (
          <Toggle key={p.id} on={provider === p.id} onClick={() => chooseProvider(p.id)}>
            {p.label}
            {p.recommended && <Badge variant="secondary">Recommended</Badge>}
          </Toggle>
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
        <ModelField
          label="Fallback model"
          feature="base"
          name="base-fallback"
          value={fallbackModel}
          provider={provider}
          endpoint={endpoint.trim()}
          apiKey={apiKey.trim()}
          onChange={setFallbackModel}
        />
        <NumberField label="Keep the call log for (days)" value={keepDays} onChange={setKeepDays} min={0} />
      </div>
    </SettingsCard>
  )
}
