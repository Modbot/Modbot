import { useCallback, useEffect, useMemo, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { api, type ApiKeysResponse, type ApiKeyView, type PermissionInfo } from '@/lib/api'
import { cn } from '@/lib/utils'
import { CopyBox } from '@/pages/Users'
import { Checkbox, Field, Outcome, Placeholder } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { failure, when } from './shared'

/**
 * Settings → API → Keys. The key is shown once, in the dialog that made it; the server keeps only
 * its hash. The permission list offers only what the signed-in person holds, because the server
 * refuses anything more.
 */
export function ApiKeysPanel() {
  const [data, setData] = useState<ApiKeysResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const load = useCallback(
    () =>
      api
        .apiKeys()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) => setError(failure(e, 'Could not load API keys.'))),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="api-keys" title="API keys">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <SettingsCard
          title="Keys"
          span={12}
          flush
          action={
            <Button size="xs" onClick={() => setCreating(true)}>
              Create key
            </Button>
          }
        >
          <KeyList keys={data.keys} onChanged={() => void load()} />
        </SettingsCard>
      )}

      <Dialog open={creating} onOpenChange={setCreating}>
        <DialogContent title="Create key">
          {data && <CreateKey grantable={data.grantable} onDone={() => void load()} />}
        </DialogContent>
      </Dialog>
    </SettingsSection>
  )
}

function KeyList({ keys, onChanged }: { keys: ApiKeyView[]; onChanged: () => void }) {
  const [problem, setProblem] = useState<string | null>(null)

  if (keys.length === 0) return <EmptyRow>No keys.</EmptyRow>

  const revoke = (id: string) => {
    setProblem(null)
    api
      .revokeApiKey(id)
      .then(onChanged)
      .catch((e: unknown) => setProblem(failure(e, 'Could not revoke the key.')))
  }

  return (
    <>
      <Table
        pinFirst
        head={
          <>
            <Th>Name</Th>
            <Th>Key</Th>
            <Th>Owner</Th>
            <Th>Permissions</Th>
            <Th>Created</Th>
            <Th>Last used</Th>
            <Th>Expires</Th>
            <Th>State</Th>
            <Th />
          </>
        }
      >
        {keys.map((k) => (
          <Tr key={k.id} className={cn(k.state !== 'active' && 'text-muted-foreground')}>
            <Td className="font-medium">
              <div className="max-w-[16rem] truncate" title={k.name}>
                {k.name}
              </div>
            </Td>
            <Td className="font-mono">{k.start}…</Td>
            <Td>{k.ownerName ?? '—'}</Td>
            <Td>{k.permissionNames.join(', ')}</Td>
            <Td className="font-mono">{when(k.createdAt)}</Td>
            <Td className={cn(k.lastUsedAt && 'font-mono')}>{k.lastUsedAt ? when(k.lastUsedAt) : 'Never'}</Td>
            <Td className={cn(k.expiresAt && 'font-mono')}>{k.expiresAt ? when(k.expiresAt) : 'Never'}</Td>
            <Td>
              <Badge variant={k.state === 'active' ? 'secondary' : 'outline'}>
                {k.state === 'active' ? 'Active' : k.state === 'expired' ? 'Expired' : 'Revoked'}
              </Badge>
            </Td>
            <Td className="text-right">
              {k.state !== 'revoked' && (
                <Button size="xs" variant="ghost" onClick={() => revoke(k.id)}>
                  Revoke
                </Button>
              )}
            </Td>
          </Tr>
        ))}
      </Table>
      {problem && (
        <div className="border-t border-t-(length:--hairline) p-(--panel-pad)">
          <Outcome tone="problem">{problem}</Outcome>
        </div>
      )}
    </>
  )
}

function CreateKey({ grantable, onDone }: { grantable: PermissionInfo[]; onDone: () => void }) {
  const [name, setName] = useState('')
  const [expires, setExpires] = useState('')
  const [chosen, setChosen] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [made, setMade] = useState<string | null>(null)

  const groups = useMemo(() => {
    const byGroup = new Map<string, PermissionInfo[]>()
    for (const p of grantable) byGroup.set(p.group, [...(byGroup.get(p.group) ?? []), p])
    return [...byGroup.entries()]
  }, [grantable])

  if (made) {
    return (
      <div className="flex flex-col gap-3">
        <p className="font-medium">Key</p>
        <CopyBox text={made} />
      </div>
    )
  }

  const toggle = (permission: string, on: boolean) =>
    setChosen((c) => (on ? [...c, permission] : c.filter((p) => p !== permission)))

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setProblem(null)

    api
      .createApiKey({
        name: name.trim(),
        permissions: chosen,
        expiresAt: expires ? new Date(`${expires}T23:59:59`).toISOString() : null,
      })
      .then((created) => {
        setMade(created.key)
        onDone()
      })
      .catch((e: unknown) => setProblem(failure(e, 'Could not create the key.')))
      .finally(() => setBusy(false))
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <Field label="Name" value={name} placeholder="Discord bot" onChange={setName} />

      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Expires</span>
        <Input type="date" value={expires} onChange={(e) => setExpires(e.target.value)} />
      </label>

      <div className="flex flex-col gap-3">
        {groups.map(([group, permissions]) => (
          <fieldset key={group} className="flex flex-col gap-1">
            <legend className="mb-1 font-medium" style={{ fontSize: 'var(--text-small)' }}>
              {group}
            </legend>
            {permissions.map((p) => (
              <Checkbox key={p.name} checked={chosen.includes(p.name)} onChange={(on) => toggle(p.name, on)}>
                {p.label}
              </Checkbox>
            ))}
          </fieldset>
        ))}
      </div>

      <div className="flex items-center gap-3">
        <Button type="submit" size="sm" disabled={busy || !name.trim() || chosen.length === 0}>
          {busy ? 'Creating…' : 'Create'}
        </Button>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </form>
  )
}
