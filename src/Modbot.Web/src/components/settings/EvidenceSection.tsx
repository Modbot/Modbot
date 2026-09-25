import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import {
  api,
  ApiError,
  type EvidenceBackendId,
  type EvidenceBackendInput,
  type EvidenceSetup,
  type EvidenceHealth,
  type EvidenceSettings,
} from '@/lib/api'
import { SwitchBank } from '@/components/ui/switch-bank'
import { Notice } from '@/components/ui/notice'
import { Checkbox, Field, Hint, Outcome, PasswordField, Placeholder, Row } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'
import { MB, bytes } from './units'

/**
 * Settings → Evidence.
 *
 * Two things on this screen are load-bearing and easy to soften by accident.
 *
 * The first is that **nothing is saved before it is proved**: pressing save runs a full write,
 * read-back, byte comparison, promote, read and delete against a store built from what is in these
 * fields, and only then does the row change. A backend that cannot do all of that cannot be
 * chosen, and the message names the step rather than saying "storage error".
 *
 * The second is the durability warning. Modbot **warns and asks; it never refuses on a
 * suspicion** — platform detection can only ever be a suspicion, because Railway, Fly.io and
 * Render all support mountable volumes and the operator is the only party who knows whether they
 * mounted one. The one refusal here is a directory that cannot be written to at all, which is a
 * fact rather than a guess. The store marker is a different mechanism entirely and is fatal:
 * absence of it is proof that the bytes are not where Modbot's records say they are.
 */
export function EvidenceSection() {
  const [data, setData] = useState<EvidenceSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .evidenceSettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to configure evidence storage.'
              : 'Could not load evidence settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="evidence" title="Evidence">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <StoreHealth health={data.health} onProbed={load} />
          <BackendCard settings={data} onSaved={load} />
          <StoreFactsCard settings={data} />
          <LimitsCard key={data.backend.storeId ?? 'none'} settings={data} onSaved={load} />
        </>
      )}
    </SettingsSection>
  )
}

/**
 * The lock, and the two states that are not it.
 *
 * "The store said no" and "the store said nothing" look similar and mean opposite things. Only the
 * first is shown as a failure: raising the same alarm every time a bucket hiccups is how an
 * operator learns to ignore the one banner that matters.
 */
function StoreHealth({ health, onProbed }: { health: EvidenceHealth; onProbed: () => void }) {
  const [probing, setProbing] = useState(false)

  const probe = () => {
    setProbing(true)
    api
      .probeEvidenceStore()
      .then(() => onProbed())
      .catch(() => onProbed())
      .finally(() => setProbing(false))
  }

  const tone = health.locked
    ? 'danger'
    : health.state === 'Unreachable'
      ? 'warn'
      : health.state === 'Healthy'
        ? 'ok'
        : 'neutral'

  return (
    <Notice
      tone={tone}
      className="col-span-12"
      title={
        health.locked
          ? 'This is not the store Modbot put its evidence in.'
          : health.state === 'Healthy'
            ? 'The store answered, and it is ours.'
            : health.state === 'Unreachable'
              ? 'The store did not answer.'
              : 'No evidence store has been configured.'
      }
      action={
        <Button size="sm" variant="outline" disabled={probing} onClick={probe}>
          {probing ? 'Checking…' : 'Re-check'}
        </Button>
      }
    >
      <p>{health.explanation}</p>
      {health.locked && <p>Uploads are blocked.</p>}
    </Notice>
  )
}

function BackendCard({ settings, onSaved }: { settings: EvidenceSettings; onSaved: () => void }) {
  const hint = settings.environmentHint
  const [backend, setBackend] = useState<EvidenceBackendId>(settings.backend.backend)
  const [root, setRoot] = useState(settings.backend.root ?? '/app/data/evidence')
  const [bucket, setBucket] = useState(settings.backend.s3.bucket ?? hint?.bucket ?? '')
  const [endpoint, setEndpoint] = useState(settings.backend.s3.endpoint ?? hint?.endpoint ?? '')
  const [accessKeyId, setAccessKeyId] = useState(
    settings.backend.s3.accessKeyId ?? hint?.accessKeyId ?? '',
  )
  const [secret, setSecret] = useState('')
  const [region, setRegion] = useState(settings.backend.s3.region ?? hint?.region ?? 'us-east-1')
  const [prefix, setPrefix] = useState(settings.backend.s3.prefix ?? '')
  const [usePathStyle, setUsePathStyle] = useState(settings.backend.s3.usePathStyle)
  const [acknowledge, setAcknowledge] = useState(false)

  const [busy, setBusy] = useState<'test' | 'save' | null>(null)
  const [result, setResult] = useState<EvidenceSetup | null>(null)
  const [failed, setFailed] = useState<string | null>(null)


  // The warning to echo back is whatever the server last put on screen, never a copy kept here.
  // What is recorded has to be the sentence the operator actually read.
  const warning = result?.requiresAcknowledgement ? result.message : null

  const body = (): EvidenceBackendInput => ({
    backend,
    root: backend === 'Filesystem' ? root.trim() : undefined,
    bucket: backend === 'S3' ? bucket.trim() : undefined,
    endpoint: backend === 'S3' ? endpoint.trim() : undefined,
    accessKeyId: backend === 'S3' ? accessKeyId.trim() : undefined,
    // Omitted rather than sent empty, so saving after fixing a typo in the endpoint keeps the
    // stored credential instead of silently clearing it.
    ...(backend === 'S3' && secret ? { secretAccessKey: secret } : {}),
    region: backend === 'S3' ? region.trim() : undefined,
    prefix: backend === 'S3' ? prefix.trim() : undefined,
    usePathStyle: backend === 'S3' ? usePathStyle : undefined,
    ...(acknowledge && warning ? { acknowledgeWarning: warning } : {}),
  })

  const run = (what: 'test' | 'save') => {
    setBusy(what)
    setResult(null)
    setFailed(null)

    const call = what === 'test' ? api.testEvidenceStore(body()) : api.setEvidenceBackend(body())

    call
      .then((r) => {
        setResult(r)
        if (r.succeeded) {
          setSecret('')
          setAcknowledge(false)
          if (what === 'save') void onSaved()
        }
      })
      .catch((e: unknown) =>
        setFailed(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to change where evidence is stored.'
            : 'Could not reach the Modbot server.',
        ),
      )
      .finally(() => setBusy(null))
  }

  return (
    <SettingsCard
      title="Where evidence is stored"
      footer={
        <>
          <Button size="xs" disabled={busy !== null} onClick={() => run('save')}>
            {busy === 'save' ? 'Testing and saving…' : 'Test and save'}
          </Button>
          <Button
            size="xs"
            variant="outline"
            disabled={busy !== null || backend === 'None'}
            onClick={() => run('test')}
          >
            {busy === 'test' ? 'Testing…' : 'Test only'}
          </Button>
          <Outcome tone="problem">{failed}</Outcome>
        </>
      }
    >
      {settings.switchBlockedReason && (
        <Notice tone="warn" title={settings.switchBlockedReason} />
      )}

      <SwitchBank
        value={backend}
        onChange={setBackend}
        options={settings.backends.map((b) => ({
          value: b.id,
          label: (
            <>
              {b.label}
              {b.recommended && ' · recommended'}
            </>
          ),
        }))}
      />

      {backend === 'Filesystem' && (
        <div className="flex max-w-lg flex-col gap-3">
          <Field label="Directory" value={root} placeholder="/app/data/evidence" onChange={setRoot} />
        </div>
      )}

      {backend === 'S3' && (
        <div className="flex max-w-lg flex-col gap-3">
          <Field label="Bucket" value={bucket} placeholder="modbot-evidence" onChange={setBucket} />
          <Field
            label="Endpoint"
            value={endpoint}
            placeholder="https://s3.example.com"
            onChange={setEndpoint}
          />
          <Field label="Access key id" value={accessKeyId} placeholder="" onChange={setAccessKeyId} />
          <PasswordField
            label={
              settings.backend.secretStored
                ? 'Secret access key (stored)'
                : 'Secret access key'
            }
            value={secret}
            onChange={setSecret}
          />
          <Field label="Region" value={region} placeholder="us-east-1" onChange={setRegion} />
          <Field label="Key prefix (optional)" value={prefix} placeholder="" onChange={setPrefix} />
          <Checkbox checked={usePathStyle} onChange={setUsePathStyle}>
            Path-style URLs
          </Checkbox>
        </div>
      )}

      {warning && (
        <Notice tone="warn" title={warning}>
          <Checkbox checked={acknowledge} onChange={setAcknowledge}>
            Use anyway
          </Checkbox>
        </Notice>
      )}

      {result && !result.requiresAcknowledgement && (
        <p
          className={result.succeeded ? 'text-ok' : 'text-destructive'}
          style={{ fontSize: 'var(--text-small)' }}
        >
          {result.failedStep && <span className="font-medium">{result.failedStep}: </span>}
          {result.message}
        </p>
      )}
    </SettingsCard>
  )
}

function StoreFactsCard({ settings }: { settings: EvidenceSettings }) {
  return (
    <SettingsCard title="What this store is doing">
      <div className="max-w-lg">
        <Row label="Store marker" value={settings.backend.storeId ?? 'none written yet'} mono={!!settings.backend.storeId} />
        <Row
          label="Delivery"
          value={
            settings.capabilities.directDeliveryAvailable ? 'Straight from the store' : 'Through Modbot'
          }
        />
        <Row
          label="Range reads"
          value={settings.capabilities.rangeRead ? 'Supported' : 'Not supported'}
        />
        <Row
          label="Evidence held"
          value={`${settings.stored.count.toLocaleString()} files, ${bytes(settings.stored.bytes)}`}
          mono
        />
        <Row
          label="Destroyed"
          value={
            settings.stored.destroyedCount === 0
              ? 'None'
              : settings.stored.destroyedCount.toLocaleString()
          }
          mono={settings.stored.destroyedCount !== 0}
        />
        <Row label="Accepted formats" value={settings.acceptedTypes.join(', ')} />
      </div>
    </SettingsCard>
  )
}

function LimitsCard({ settings, onSaved }: { settings: EvidenceSettings; onSaved: () => void }) {
  const current = settings.limits
  const [perFile, setPerFile] = useState(String(Math.round(current.maxFileBytes / MB)))
  const [perReport, setPerReport] = useState(String(Math.round(current.maxReportBytes / MB)))
  const [perDeployment, setPerDeployment] = useState(
    String(Math.round(current.maxDeploymentBytes / MB)),
  )
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  const dirty =
    perFile !== String(Math.round(current.maxFileBytes / MB)) ||
    perReport !== String(Math.round(current.maxReportBytes / MB)) ||
    perDeployment !== String(Math.round(current.maxDeploymentBytes / MB))

  const save = () => {
    setSaving(true)
    setProblem(null)
    setSaved(false)

    api
      .setEvidenceLimits({
        maxFileBytes: Number(perFile) * MB,
        maxReportBytes: Number(perReport) * MB,
        maxDeploymentBytes: Number(perDeployment) * MB,
      })
      .then(() => {
        setSaved(true)
        void onSaved()
      })
      .catch((e: unknown) =>
        setProblem(e instanceof ApiError ? e.message : 'Could not save the limits.'),
      )
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard
      title="File upload limits"
      span={12}
      footer={
        <>
          <Button size="xs" disabled={!dirty || saving} onClick={save}>
            {saving ? 'Saving…' : 'Save limits'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="grid max-w-md items-end gap-3 sm:grid-cols-3">
        <Field label="Per file (MB)" value={perFile} placeholder="100" onChange={setPerFile} />
        <Field label="Per report (MB)" value={perReport} placeholder="0" onChange={setPerReport} />
        <Field
          label="Deployment (MB)"
          value={perDeployment}
          placeholder="0"
          onChange={setPerDeployment}
        />
      </div>
      <Hint>Per report and deployment: 0 means no limit.</Hint>
    </SettingsCard>
  )
}
