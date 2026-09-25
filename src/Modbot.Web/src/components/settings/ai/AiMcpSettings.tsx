import { useCallback, useEffect, useState } from 'react'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { CodeBlock } from '@/components/CodeBlock'
import { api, ApiError, type McpConnection, type McpSettings as Settings } from '@/lib/api'
import { CopyBox } from '@/pages/Users'
import { cn } from '@/lib/utils'
import { failure, when } from '../api/shared'
import { Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

const headClass = 'h-(--row-h) px-(--panel-pad) font-normal whitespace-nowrap'
const cellClass = 'px-(--panel-pad) py-1.5'

/**
 * Settings → AI → MCP (MCP server design): the switch and the address, the connected apps, and
 * what to paste into each AI app.
 *
 * The hosted chats take the address alone and sign in through Modbot; local tools take a JSON
 * entry, with an API key in a header or with sign-in. Both are shown filled in, so the only work
 * left is copying.
 */
export function AiMcpSettings() {
  const [data, setData] = useState<Settings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .mcpSettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change AI settings.'
              : 'Could not load MCP settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="ai-mcp" title="MCP server settings">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <ServerCard settings={data} onSaved={setData} />
          <ConnectionsCard />
          <HostedAppsCard serverUrl={data.serverUrl} />
          <LocalAppsCard settings={data} />
        </>
      )}
    </SettingsSection>
  )
}

function ServerCard({ settings, onSaved }: { settings: Settings; onSaved: (next: Settings) => void }) {
  const [enabled, setEnabled] = useState(settings.enabled)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setMcpSettings({ enabled })
      .then((next) => {
        setSaved(true)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not save.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="MCP server"
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
        MCP server on
      </Switch>

      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Server URL</span>
        <CopyBox text={settings.serverUrl} />
      </label>
      {!settings.publicAddressSet && <Outcome tone="problem">Public address is not set.</Outcome>}
    </SettingsCard>
  )
}

function ConnectionsCard() {
  const [connections, setConnections] = useState<McpConnection[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .mcpConnections()
        .then((d) => setConnections(d.connections))
        .catch((e: unknown) => setProblem(failure(e, 'Could not load connected apps.'))),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const disconnect = (id: string) => {
    setProblem(null)
    api
      .disconnectMcp(id)
      .then(load)
      .catch((e: unknown) => setProblem(failure(e, 'Could not disconnect.')))
  }

  return (
    <SettingsCard title="Connected apps" flush>
      {connections === null ? (
        <EmptyRow>Loading…</EmptyRow>
      ) : connections.length === 0 ? (
        <EmptyRow>None.</EmptyRow>
      ) : (
        <div className="relative overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="bg-strip text-left text-muted-foreground">
              <tr className="border-b border-b-(length:--hairline)">
                <th className={headClass}>App</th>
                <th className={headClass}>Connected</th>
                <th className={headClass}>Last used</th>
                <th className={headClass}>Expires</th>
                <th className={headClass} />
              </tr>
            </thead>
            <tbody>
              {connections.map((c) => (
                <tr key={c.id} className="border-b border-b-(length:--hairline) last:border-0">
                  <td className={cn(cellClass, 'font-medium')}>{c.clientName}</td>
                  <td className={cn(cellClass, 'font-mono')}>{when(c.connectedAt)}</td>
                  <td className={cn(cellClass, c.lastUsedAt && 'font-mono')}>{c.lastUsedAt ? when(c.lastUsedAt) : 'Never'}</td>
                  <td className={cn(cellClass, 'font-mono')}>{when(c.expiresAt)}</td>
                  <td className={cn(cellClass, 'text-right')}>
                    <Button size="xs" variant="ghost" onClick={() => disconnect(c.id)}>
                      Disconnect
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {problem && (
        <div className="p-(--panel-pad)">
          <Outcome tone="problem">{problem}</Outcome>
        </div>
      )}
    </SettingsCard>
  )
}

/**
 * Where each hosted chat adds a remote MCP server (research: 2026-09-17-mcp-connectors.md §2).
 * Each takes the address alone and signs in through Modbot. Grok's chat has no such page.
 */
const HOSTED = [
  { name: 'Claude.ai', open: 'https://claude.ai/settings/connectors' },
  { name: 'ChatGPT', open: 'https://chatgpt.com/#settings' },
  { name: 'Gemini', open: 'https://gemini.google.com/' },
  { name: 'Grok', open: null },
] as const

function HostedAppsCard({ serverUrl }: { serverUrl: string }) {
  return (
    <SettingsCard title="Chat apps" span={12} flush>
      <PanelGrid className="m-0 md:grid-cols-2">
        {HOSTED.map((app) => (
          <div key={app.name} className="flex flex-col gap-2 p-(--panel-pad)">
            <div className="flex items-center justify-between gap-3">
              <span className="font-medium">{app.name}</span>
              {app.open ? (
                <Button asChild size="sm" variant="outline">
                  <a href={app.open} target="_blank" rel="noopener noreferrer">
                    Open {app.name}
                  </a>
                </Button>
              ) : (
                <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Not supported
                </span>
              )}
            </div>
            {app.open && <CopyBox text={serverUrl} />}
          </div>
        ))}
      </PanelGrid>
    </SettingsCard>
  )
}

function LocalAppsCard({ settings }: { settings: Settings }) {
  const [key, setKey] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const createKey = () => {
    setBusy(true)
    setProblem(null)

    api
      .createApiKey({ name: 'MCP', permissions: settings.keyPermissions, expiresAt: null })
      .then((made) => setKey(made.key))
      .catch((e: unknown) => setProblem(failure(e, 'Could not create the key.')))
      .finally(() => setBusy(false))
  }

  const bearer = key ?? 'mbk_…'
  const withKey = {
    mcpServers: { modbot: { type: 'http', url: settings.serverUrl, headers: { Authorization: `Bearer ${bearer}` } } },
  }
  const withSignIn = { mcpServers: { modbot: { type: 'http', url: settings.serverUrl } } }

  const cursor =
    'cursor://anysphere.cursor-deeplink/mcp/install?name=modbot&config=' +
    btoa(JSON.stringify({ url: settings.serverUrl }))
  const vscode = 'vscode:mcp/install?' + encodeURIComponent(JSON.stringify({ name: 'modbot', type: 'http', url: settings.serverUrl }))

  return (
    <SettingsCard
      title="Local apps"
      span={12}
      action={
        <Button size="xs" disabled={busy} onClick={createKey}>
          {busy ? 'Creating…' : 'Create key'}
        </Button>
      }
    >
      {key && (
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Key</span>
          <CopyBox text={key} />
        </label>
      )}
      <Outcome tone="problem">{problem}</Outcome>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="min-w-0">
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            With API key
          </div>
          <CodeBlock>
            <code className="language-json">{JSON.stringify(withKey, null, 2)}</code>
          </CodeBlock>
          <CodeBlock>
            <code className="language-bash">
              {`claude mcp add --transport http modbot ${settings.serverUrl} --header "Authorization: Bearer ${bearer}"`}
            </code>
          </CodeBlock>
        </div>

        <div className="min-w-0">
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            With sign-in
          </div>
          <CodeBlock>
            <code className="language-json">{JSON.stringify(withSignIn, null, 2)}</code>
          </CodeBlock>
          <CodeBlock>
            <code className="language-bash">{`claude mcp add --transport http modbot ${settings.serverUrl}`}</code>
          </CodeBlock>
          <div className="mt-2 flex flex-wrap gap-2">
            <Button asChild size="sm" variant="outline">
              <a href={cursor}>Add to Cursor</a>
            </Button>
            <Button asChild size="sm" variant="outline">
              <a href={vscode}>Add to VS Code</a>
            </Button>
          </div>
        </div>
      </div>
    </SettingsCard>
  )
}
