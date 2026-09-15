import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api } from '@/lib/api'
import { CopyBox } from '@/pages/Users'
import { Field, Outcome, PasswordField } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { failure, when } from './shared'

type Received = { at: number; kind: string; text: string; type?: string; occurredAt?: string }

type Status = 'closed' | 'connecting' | 'open'

const KEEP = 200

/**
 * Settings → API → Events: the WebSocket and long polling addresses, and a live view that connects
 * the way a browser has to -- a ticket from a POST, never a key in the address. With a key typed
 * in, the ticket stands for that key, so what arrives is what the key may see. Long polling takes a
 * key in the header only, so the browser view uses the socket.
 */
export function EventsPanel() {
  const address = `${window.location.protocol === 'https:' ? 'wss' : 'ws'}://${window.location.host}/api/events/ws`
  const pollAddress = `${window.location.origin}/api/events/poll`

  const [key, setKey] = useState('')
  const [types, setTypes] = useState('*')
  const [cursor, setCursor] = useState('')
  const [status, setStatus] = useState<Status>('closed')
  const [closed, setClosed] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [received, setReceived] = useState<Received[]>([])
  const socket = useRef<WebSocket | null>(null)
  const counter = useRef(0)

  useEffect(() => () => socket.current?.close(), [])

  const push = (item: Omit<Received, 'at'>) =>
    setReceived((list) => [{ ...item, at: counter.current++ }, ...list].slice(0, KEEP))

  const connect = () => {
    setProblem(null)
    setClosed(null)
    setStatus('connecting')

    api
      .eventTicket(key.trim() || undefined)
      .then(({ ticket }) => {
        const ws = new WebSocket(`${address}?ticket=${encodeURIComponent(ticket)}`)
        socket.current = ws

        ws.onmessage = (message) => {
          let parsed: { kind?: string; event?: { type?: string; occurred_at?: string }; cursor?: string }
          try {
            parsed = JSON.parse(String(message.data))
          } catch {
            return
          }

          if (parsed.kind === 'hello') {
            setStatus('open')
            ws.send(
              JSON.stringify({
                op: 'subscribe',
                types: types
                  .split(/[\n,]/)
                  .map((t) => t.trim())
                  .filter(Boolean),
                ...(cursor.trim() ? { cursor: cursor.trim() } : {}),
              }),
            )
          }

          push({
            kind: parsed.kind ?? '?',
            text: String(message.data),
            type: parsed.event?.type,
            occurredAt: parsed.event?.occurred_at,
          })
        }

        ws.onclose = (event) => {
          setStatus('closed')
          setClosed(event.code === 1000 || event.code === 1005 ? null : `${event.code} ${event.reason}`.trim())
          socket.current = null
        }
      })
      .catch((e: unknown) => {
        setStatus('closed')
        setProblem(failure(e, 'Could not get a ticket.'))
      })
  }

  const disconnect = () => socket.current?.close(1000)

  return (
    <SettingsSection id="api-events" title="Events">
      <SettingsCard title="Addresses">
        <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">WebSocket</span>
          <CopyBox text={address} />
        </div>
        <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Long polling</span>
          <CopyBox text={pollAddress} />
        </div>
      </SettingsCard>

      <SettingsCard
        title="Test"
        footer={
          <>
            {status === 'closed' ? (
              <Button size="sm" onClick={connect}>
                Connect
              </Button>
            ) : (
              <Button size="sm" variant="outline" onClick={disconnect}>
                Disconnect
              </Button>
            )}
            <Button size="sm" variant="outline" disabled={received.length === 0} onClick={() => setReceived([])}>
              Clear
            </Button>
            <Outcome tone="ok">{status === 'open' && 'Connected'}</Outcome>
            <Outcome tone="problem">{status === 'connecting' ? null : (closed ?? problem)}</Outcome>
          </>
        }
      >
        <div className="flex max-w-lg flex-col gap-3">
          <PasswordField label="API key" value={key} onChange={setKey} />
          <Field label="Event types" value={types} placeholder="*" onChange={setTypes} />
          <Field label="Cursor" value={cursor} placeholder="" onChange={setCursor} />
        </div>
      </SettingsCard>

      <SettingsCard title="Received" span={12}>
        {received.length === 0 ? (
          <p className="text-muted-foreground">Nothing yet.</p>
        ) : (
          <div className="flex max-h-[32rem] flex-col divide-y overflow-auto" style={{ fontSize: 'var(--text-small)' }}>
            {received.map((r) => (
              <details key={r.at} className="py-1.5">
                <summary className="cursor-pointer">
                  <span className="font-medium">{r.kind}</span>
                  {r.type && <span className="ml-2 font-mono">{r.type}</span>}
                  {r.occurredAt && <span className="ml-2 text-muted-foreground">{when(r.occurredAt)}</span>}
                </summary>
                <pre className="mt-1 overflow-x-auto whitespace-pre-wrap break-all font-mono text-muted-foreground">
                  {JSON.stringify(JSON.parse(r.text), null, 2)}
                </pre>
              </details>
            ))}
          </div>
        )}
      </SettingsCard>
    </SettingsSection>
  )
}
