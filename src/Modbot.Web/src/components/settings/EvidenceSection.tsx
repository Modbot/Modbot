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
import { cn } from '@/lib/utils'
import { Field, PasswordField, Placeholder, Row, Section } from './fields'
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

  if (error) return <Placeholder>{error}</Placeholder>
  if (!data) return <Placeholder>Loading…</Placeholder>

  return (
    <div className="flex flex-col gap-4">
      <StoreHealth health={data.health} onProbed={load} />
      <BackendForm settings={data} onSaved={load} />
      <StoreFacts settings={data} />
      <LimitsForm key={data.backend.storeId ?? 'none'} settings={data} onSaved={load} />

      <p className="max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {data.durabilityStatement}
      </p>
    </div>
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
    ? 'border-destructive/40 bg-destructive/10'
    : health.state === 'Unreachable'
      ? 'border-warn/40 bg-warn/10'
      : health.state === 'Healthy'
        ? 'border-ok/40 bg-ok/10'
        : 'border-border bg-secondary'

  return (
    <div className={cn('rounded-lg border px-4 py-3', tone)} style={{ borderWidth: 'var(--hairline)' }}>
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="font-medium">
            {health.locked
              ? 'This is not the store Modbot put its evidence in.'
              : health.state === 'Healthy'
                ? 'The store answered, and it is ours.'
                : health.state === 'Unreachable'
                  ? 'The store did not answer.'
                  : 'No evidence store has been configured.'}
          </div>
          <p
            className="mt-1 max-w-3xl text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {health.explanation}
          </p>
          {health.locked && (
            <p
              className="mt-1 max-w-3xl text-muted-foreground"
              style={{ fontSize: 'var(--text-small)' }}
            >
              Uploads are refused while this is true. Nothing else is affected — bans, audit
              ingest, Discord, the overlay and analytics are all still running. Fixing the store
              here and saving clears it; the incident stays on the record either way.
            </p>
          )}
        </div>
        <Button size="sm" variant="outline" disabled={probing} onClick={probe}>
          {probing ? 'Checking…' : 'Re-check'}
        </Button>
      </div>
    </div>
  )
}

function BackendForm({ settings, onSaved }: { settings: EvidenceSettings; onSaved: () => void }) {
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

  const chosen = settings.backends.find((b) => b.id === backend)

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
    <Section title="Where evidence is stored">
      {settings.switchBlockedReason && (
        <div
          className="mb-3 rounded-lg border border-warn/40 bg-warn/10 px-4 py-3"
          style={{ borderWidth: 'var(--hairline)' }}
        >
          <div className="font-medium">The backend cannot be changed while objects are stored.</div>
          <p
            className="mt-1 max-w-3xl text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {settings.switchBlockedReason}
          </p>
        </div>
      )}

      <div role="group" className="mb-3 flex flex-wrap gap-1.5">
        {settings.backends.map((b) => (
          <button
            key={b.id}
            type="button"
            aria-pressed={backend === b.id}
            onClick={() => setBackend(b.id)}
            className={cn(
              'inline-flex items-center rounded-full border px-2.5 font-medium transition-colors',
              backend === b.id
                ? 'border-transparent bg-accent text-accent-foreground'
                : 'text-muted-foreground hover:text-foreground',
            )}
            style={{
              fontSize: 'var(--text-small)',
              borderWidth: 'var(--hairline)',
              height: 'calc(var(--control-h) - 6px)',
            }}
          >
            {b.label}
            {b.recommended && ' · recommended'}
          </button>
        ))}
      </div>

      {chosen && (
        <p className="mb-3 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {chosen.summary}
          {chosen.caution && <span className="text-warn"> {chosen.caution}</span>}
        </p>
      )}

      {hint && backend === 'S3' && (
        <p className="mb-3 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          These were pre-filled from this container's environment variables, and the secret is
          already held there too. Nothing is stored until you press save, and the environment is
          never read again afterwards — the database decides from then on.
        </p>
      )}

      {backend === 'Filesystem' && (
        <div className="flex max-w-xl flex-col gap-3">
          <Field label="Directory" value={root} placeholder="/app/data/evidence" onChange={setRoot} />
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Mount a Docker volume here yourself. Modbot declares no VOLUME in its image on purpose:
            an anonymous volume would make an unconfigured host appear to work and lose everything
            the first time the container was recreated.
          </p>
        </div>
      )}

      {backend === 'S3' && (
        <div className="flex max-w-xl flex-col gap-3">
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
                ? 'Secret access key (leave blank to keep the stored one)'
                : 'Secret access key'
            }
            value={secret}
            onChange={setSecret}
          />
          <Field label="Region" value={region} placeholder="us-east-1" onChange={setRegion} />
          <Field label="Key prefix (optional)" value={prefix} placeholder="" onChange={setPrefix} />
          <label className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
            <input
              type="checkbox"
              checked={usePathStyle}
              onChange={(e) => setUsePathStyle(e.target.checked)}
            />
            Path-style URLs (https://endpoint/bucket/key)
          </label>
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Providers differ, and one provider differs from itself: Railway issues virtual-hosted
            URLs on new buckets and path-style on older ones, and only its credentials tab says
            which. MinIO defaults to path-style. If the round trip fails on the endpoint, this is
            the first thing to try.
          </p>
        </div>
      )}

      {backend === 'Database' && (
        <p className="max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Nothing to configure — the evidence goes in the database Modbot is already using, in
          chunked rows. There is no second credential and no second service to forget about when
          the deployment is handed to the next volunteer, and that is the whole of the case for it.
        </p>
      )}

      {warning && (
        <div
          className="mt-3 rounded-lg border border-warn/40 bg-warn/10 px-4 py-3"
          style={{ borderWidth: 'var(--hairline)' }}
        >
          <div className="font-medium">Modbot could not prove this directory persists.</div>
          <p
            className="mt-1 max-w-3xl text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {warning}
          </p>
          <label className="mt-2 flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
            <input
              type="checkbox"
              checked={acknowledge}
              onChange={(e) => setAcknowledge(e.target.checked)}
            />
            Use anyway — recorded against your name, with this warning kept word for word
          </label>
        </div>
      )}

      {result && !result.requiresAcknowledgement && (
        <p
          className={cn('mt-3 max-w-3xl', result.succeeded ? 'text-ok' : 'text-destructive')}
          style={{ fontSize: 'var(--text-small)' }}
        >
          {result.failedStep && <span className="font-medium">{result.failedStep}: </span>}
          {result.message}
        </p>
      )}

      {failed && (
        <p className="mt-3 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {failed}
        </p>
      )}

      <div className="mt-3 flex items-center gap-3">
        <Button size="sm" disabled={busy !== null} onClick={() => run('save')}>
          {busy === 'save' ? 'Testing and saving…' : 'Test and save'}
        </Button>
        <Button
          size="sm"
          variant="outline"
          disabled={busy !== null || backend === 'None'}
          onClick={() => run('test')}
        >
          {busy === 'test' ? 'Testing…' : 'Test only'}
        </Button>
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Saving writes a test object, reads it back, compares the bytes and deletes it first.
        </span>
      </div>
    </Section>
  )
}

function StoreFacts({ settings }: { settings: EvidenceSettings }) {
  const durability = settings.durability

  return (
    <Section title="What this store is doing">
      <Row label="Store marker" value={settings.backend.storeId ?? 'none written yet'} />
      <Row label="Delivery" value={settings.capabilities.deliveryExplanation} />
      <Row
        label="Range reads"
        value={
          settings.capabilities.rangeRead
            ? 'Supported, so video seeks rather than only playing from the start'
            : 'Not supported by this backend'
        }
      />
      <Row
        label="Evidence held"
        value={`${settings.stored.count.toLocaleString()} files, ${bytes(settings.stored.bytes)}`}
      />
      <Row
        label="Destroyed"
        value={
          settings.stored.destroyedCount === 0
            ? 'None'
            : `${settings.stored.destroyedCount.toLocaleString()} — the records are kept, the bytes are not`
        }
      />
      <Row label="Accepted formats" value={settings.acceptedTypes.join(', ')} />

      {durability && (
        <p className="mt-3 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {durability.message}
          {durability.acknowledgedBy && (
            <>
              {' '}
              Acknowledged by {durability.acknowledgedBy}
              {durability.acknowledgedAt &&
                ` on ${new Date(durability.acknowledgedAt).toLocaleString()}`}
              .
            </>
          )}
        </p>
      )}
    </Section>
  )
}

function LimitsForm({ settings, onSaved }: { settings: EvidenceSettings; onSaved: () => void }) {
  const current = settings.limits
  const [perFile, setPerFile] = useState(String(Math.round(current.maxFileBytes / MB)))
  const [perReport, setPerReport] = useState(String(Math.round(current.maxReportBytes / MB)))
  const [perDeployment, setPerDeployment] = useState(
    String(Math.round(current.maxDeploymentBytes / MB)),
  )
  const [direct, setDirect] = useState(current.directDeliveryEnabled)
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  const dirty =
    perFile !== String(Math.round(current.maxFileBytes / MB)) ||
    perReport !== String(Math.round(current.maxReportBytes / MB)) ||
    perDeployment !== String(Math.round(current.maxDeploymentBytes / MB)) ||
    direct !== current.directDeliveryEnabled

  const save = () => {
    setSaving(true)
    setProblem(null)
    setSaved(false)

    api
      .setEvidenceLimits({
        maxFileBytes: Number(perFile) * MB,
        maxReportBytes: Number(perReport) * MB,
        maxDeploymentBytes: Number(perDeployment) * MB,
        directDeliveryEnabled: direct,
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
    <Section title="Limits">
      <div className="flex max-w-xl flex-col gap-3">
        <Field label="Per file (MB)" value={perFile} placeholder="100" onChange={setPerFile} />
        <Field
          label="Per report (MB, 0 for no limit)"
          value={perReport}
          placeholder="0"
          onChange={setPerReport}
        />
        <Field
          label="Across the deployment (MB, 0 for no limit)"
          value={perDeployment}
          placeholder="0"
          onChange={setPerDeployment}
        />

        <label className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
          <input
            type="checkbox"
            checked={direct}
            disabled={!settings.capabilities.presignedRead}
            onChange={(e) => setDirect(e.target.checked)}
          />
          Let the store deliver evidence to the browser directly
        </label>

        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {settings.capabilities.deliveryExplanation}
        </p>
      </div>

      <div className="mt-3 flex items-center gap-3">
        <Button size="sm" disabled={!dirty || saving} onClick={save}>
          {saving ? 'Saving…' : 'Save limits'}
        </Button>
        {saved && (
          <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>
            Saved.
          </span>
        )}
        {problem && (
          <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {problem}
          </span>
        )}
      </div>
    </Section>
  )
}
