import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { JsonView } from '@/components/JsonView'
import { api, ApiError, type VRChatProxyAnswer, type VRChatProxySettings as Settings } from '@/lib/api'
import { CopyBox } from '@/pages/Users'
import { failure } from '../api/shared'
import { LongField, Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → VRChat Proxy (VRChat proxy design): the switch, the address a program puts in front
 * of a VRChat path, and a playground that sends one request through the proxy as the signed-in
 * person and shows what VRChat answered.
 *
 * The reasoning -- whose session a request goes out on, how it is paced, what is never
 * forwarded -- is in .agent/specs/2026-09-17-vrchat-proxy-and-moderator-buckets-design.md and in
 * docs/content/docs/api/vrchat-proxy.mdx; the screen carries labels only.
 */
export function VRChatProxySection() {
  const [data, setData] = useState<Settings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .vrchatProxySettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change settings.'
              : 'Could not load the VRChat proxy settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="proxy" title="VRChat Proxy">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <ProxyCard settings={data} onSaved={setData} />
          <Playground enabled={data.enabled} />
        </>
      )}
    </SettingsSection>
  )
}

function ProxyCard({ settings, onSaved }: { settings: Settings; onSaved: (next: Settings) => void }) {
  const [enabled, setEnabled] = useState(settings.enabled)
  const [imagesProxied, setImagesProxied] = useState(settings.imagesProxied)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setVRChatProxySettings({ enabled, imagesProxied })
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not save.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="VRChat proxy"
      span={12}
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
      <Switch checked={enabled} onChange={setEnabled}>
        VRChat proxy on
      </Switch>

      <Switch checked={imagesProxied} onChange={setImagesProxied}>
        Proxy VRChat images through Modbot
      </Switch>

      <label className="flex max-w-lg flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Base URL</span>
        <CopyBox text={settings.baseUrl} />
      </label>
      {!settings.publicAddressSet && <Outcome tone="problem">Public address is not set.</Outcome>}
    </SettingsCard>
  )
}

const METHODS = ['GET', 'POST', 'PUT', 'DELETE'] as const

type Method = (typeof METHODS)[number]

function Playground({ enabled }: { enabled: boolean }) {
  const [method, setMethod] = useState<Method>('GET')
  const [path, setPath] = useState('api/1/users/')
  const [body, setBody] = useState('')
  const [busy, setBusy] = useState(false)
  const [answer, setAnswer] = useState<VRChatProxyAnswer | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const takesBody = method === 'POST' || method === 'PUT'

  const send = () => {
    setBusy(true)
    setProblem(null)
    setAnswer(null)

    api
      .vrchatProxy(method, path.trim(), takesBody ? body : undefined)
      .then(setAnswer)
      .catch((e: unknown) => setProblem(failure(e, 'Could not send the request.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Playground"
      span={12}
      footer={
        <>
          <Button size="xs" disabled={busy || !enabled || !path.trim()} onClick={send}>
            {busy ? 'Sending…' : 'Send'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Method</span>
          <Select
            value={method}
            onChange={(m) => setMethod(m as Method)}
            className="font-mono"
            aria-label="Method"
          >
            {METHODS.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </Select>
        </label>

        <label className="flex min-w-[16rem] flex-1 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Path</span>
          <Input
            value={path}
            onChange={(e) => setPath(e.target.value)}
            className="font-mono"
            onKeyDown={(e) => {
              if (e.key === 'Enter' && enabled && path.trim() && !busy) send()
            }}
          />
        </label>
      </div>

      {takesBody && <LongField label="Body" value={body} placeholder="{}" rows={6} onChange={setBody} />}

      {answer && <Answer answer={answer} />}
    </SettingsCard>
  )
}

function Answer({ answer }: { answer: VRChatProxyAnswer }) {
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-3" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Status</span>
        <span className={answer.status < 400 ? 'font-mono font-medium tabular-nums' : 'font-mono font-medium tabular-nums text-destructive'}>
          {answer.status}
        </span>
        {answer.account && (
          <>
            <span className="text-muted-foreground">Account</span>
            <span className="font-medium">{answer.account === 'own' ? 'Own' : 'Service'}</span>
          </>
        )}
      </div>
      {/* A body VRChat sent as something other than JSON -- an error page, an empty 204 -- is
          shown as it came: the viewer only re-indents and colours what parses. */}
      <JsonView title="Answer" text={answer.text} />
    </div>
  )
}
