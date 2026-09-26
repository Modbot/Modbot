import { useCallback, useEffect, useMemo, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { PanelGrid } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { ApiError, api, type CurrentUser, type PermissionInfo, type RoleView } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Empty } from '@/pages/Members'
import { ErrorText, Field } from '@/pages/setup/WizardChrome'
import { Notice } from '@/components/ui/notice'

/**
 * Roles: a name and a checklist of what it allows (accounts and access design §3, §8).
 *
 * The labels and one-line descriptions come from the server's catalogue, so the words on this
 * page are the words in the API. Administrator is shown and cannot be touched; the other built-in
 * roles keep their names and can change what they allow; custom roles can do anything.
 */
export function Roles({ me }: { me: CurrentUser }) {
  const [roles, setRoles] = useState<RoleView[] | null>(null)
  const [catalogue, setCatalogue] = useState<PermissionInfo[]>([])
  const [error, setError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const refresh = useCallback(
    () =>
      api
        .roles()
        .then((r) => {
          setRoles(r.roles)
          setCatalogue(r.permissions)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load roles.')),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!roles) return <Empty>Loading…</Empty>

  return (
    <div className="flex flex-col gap-3">
      <div className="flex justify-end">
        <Button onClick={() => setCreating(true)} disabled={creating}>
          New role
        </Button>
      </div>

      <PanelGrid className="grid-cols-1">
        {creating && (
          <RoleEditor
            catalogue={catalogue}
            me={me}
            onCancel={() => setCreating(false)}
            onSaved={() => {
              setCreating(false)
              void refresh()
            }}
          />
        )}

        {roles.map((r) => (
          <RoleEditor key={r.id} role={r} catalogue={catalogue} me={me} onSaved={() => void refresh()} />
        ))}
      </PanelGrid>
    </div>
  )
}

function RoleEditor({
  role,
  catalogue,
  me,
  onSaved,
  onCancel,
}: {
  role?: RoleView
  catalogue: PermissionInfo[]
  me: CurrentUser
  onSaved: () => void
  onCancel?: () => void
}) {
  const [name, setName] = useState(role?.name ?? '')
  const [description, setDescription] = useState(role?.description ?? '')
  const [permissions, setPermissions] = useState<string[]>(role?.permissionNames ?? [])
  const [open, setOpen] = useState(role === undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const isAdmin = me.permissionNames.includes('Administrator')
  const locked = role?.locked ?? false
  const canTick = (p: PermissionInfo) => isAdmin || me.permissionNames.includes(p.name)

  const groups = useMemo(() => {
    const byGroup = new Map<string, PermissionInfo[]>()
    for (const p of catalogue) byGroup.set(p.group, [...(byGroup.get(p.group) ?? []), p])
    return [...byGroup.entries()]
  }, [catalogue])

  const dirty =
    !role ||
    name !== role.name ||
    description !== role.description ||
    permissions.length !== role.permissionNames.length ||
    permissions.some((p) => !role.permissionNames.includes(p))

  const save = () => {
    setBusy(true)
    setError(null)
    const body = { name, description, permissions }
    ;(role ? api.updateRole(role.id, body) : api.createRole(body))
      .then(() => {
        setOpen(role === undefined ? false : open)
        onSaved()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the role.'))
      .finally(() => setBusy(false))
  }

  const remove = () => {
    if (!role) return
    setBusy(true)
    setError(null)
    api
      .deleteRole(role.id)
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not delete the role.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader className={cn('p-0', !open && 'border-b-0 bg-card')}>
        <button
          type="button"
          className="flex w-full items-center gap-3 px-(--panel-pad) py-1.5 text-left hover:bg-muted/50"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
        >
          <div className="min-w-0">
            <div className="font-label">
              {role?.name ?? 'New role'}
              {role?.isBuiltIn && (
                <Badge variant="outline" className="ml-2">
                  built in
                </Badge>
              )}
            </div>
            {role?.description && (
              <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {role.description}
              </div>
            )}
          </div>
          <div className="flex-1" />
          {role && (
            <span className="shrink-0 text-right text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {role.locked ? (
                'everything'
              ) : (
                <span className="whitespace-nowrap">
                  <span className="font-mono">{role.permissionNames.length}</span> permission
                  {role.permissionNames.length === 1 ? '' : 's'}
                </span>
              )}
              {' · '}
              <span className="whitespace-nowrap">
                <span className="font-mono">{role.userCount}</span> {role.userCount === 1 ? 'person' : 'people'}
              </span>
            </span>
          )}
        </button>
      </CardHeader>

      {open && (
        <CardContent>
          <div className="space-y-4" style={{ fontSize: 'var(--text-small)' }}>
            {locked ? (
              <Notice>Allows everything. Cannot be changed.</Notice>
            ) : (
              <>
                <div className="grid items-end gap-3 sm:grid-cols-[1fr_2fr]">
                  <Field label="Name" htmlFor={`role-name-${role?.id ?? 'new'}`}>
                    <Input
                      id={`role-name-${role?.id ?? 'new'}`}
                      value={name}
                      disabled={role?.isBuiltIn}
                      title={role?.isBuiltIn ? 'Built-in roles keep their names.' : undefined}
                      onChange={(e) => setName(e.target.value)}
                    />
                  </Field>
                  <Field label="One line about it" htmlFor={`role-desc-${role?.id ?? 'new'}`}>
                    <Input
                      id={`role-desc-${role?.id ?? 'new'}`}
                      value={description}
                      onChange={(e) => setDescription(e.target.value)}
                    />
                  </Field>
                </div>

                <div className="grid gap-4 sm:grid-cols-2">
                  {groups.map(([group, items]) => (
                    <div key={group}>
                      <div className="mb-1 font-label text-muted-foreground">
                        {group}
                      </div>
                      <div className="space-y-1">
                        {items.map((p) => {
                          const allowed = canTick(p)
                          return (
                            <Checkbox
                              key={p.name}
                              disabled={!allowed}
                              title={allowed ? p.description : 'You can only put permissions into a role that you have yourself.'}
                              checked={permissions.includes(p.name)}
                              onChange={(on) =>
                                setPermissions(on ? [...permissions, p.name] : permissions.filter((n) => n !== p.name))
                              }
                            >
                              <span className="font-medium">{p.label}</span>
                              <span className="text-muted-foreground"> · {p.description}</span>
                            </Checkbox>
                          )
                        })}
                      </div>
                    </div>
                  ))}
                </div>

                <ErrorText>{error}</ErrorText>

                <div className="flex items-center gap-2">
                  <Button size="sm" onClick={save} disabled={busy || !dirty || !name.trim()}>
                    {busy ? 'Saving…' : role ? 'Save changes' : 'Create role'}
                  </Button>
                  {onCancel && (
                    <Button size="sm" variant="ghost" onClick={onCancel} disabled={busy}>
                      Cancel
                    </Button>
                  )}
                  <div className="flex-1" />
                  {role && !role.isBuiltIn && (
                    <Button
                      size="sm"
                      variant="destructive"
                      onClick={remove}
                      disabled={busy || role.userCount > 0}
                      title={role.userCount > 0 ? 'Move the people who hold it to another role first.' : undefined}
                    >
                      Delete role
                    </Button>
                  )}
                </div>
              </>
            )}
          </div>
        </CardContent>
      )}
    </Card>
  )
}
