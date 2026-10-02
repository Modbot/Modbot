import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronDown, ChevronUp } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { PanelGrid } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { ApiError, api, type CurrentUser, type PermissionInfo, type RoleView } from '@/lib/api'
import { cn } from '@/lib/utils'
import { can, FIRST_POSITION, isBelowMe } from '@/lib/permissions'
import { Empty } from '@/components/ListParts'
import { ErrorText, Field } from '@/pages/setup/WizardChrome'
import { Notice } from '@/components/ui/notice'
import { ConfirmButton } from '@/components/settings/fields'
import { RoleDiscordRoles } from '@/components/staffRoles/StaffRoles'

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
  const [moving, setMoving] = useState(false)
  const [moveError, setMoveError] = useState<string | null>(null)

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

  if (error) return <Empty tone="danger" onTryAgain={refresh}>{error}</Empty>
  if (!roles) return <Empty tone="loading" />

  // A role can go up or down only when it, and the role it changes places with, are below your
  // highest role: otherwise it would take your own place or go above it (design §3.5). Administrator
  // stays first, and nothing goes above it.
  const canMove = (i: number, direction: 'up' | 'down') => {
    const role = roles[i]
    if (role.locked || role.position === FIRST_POSITION || !isBelowMe(me, role.position)) return false
    if (direction === 'down') return i < roles.length - 1
    return i > 0 && roles[i - 1].position !== FIRST_POSITION && isBelowMe(me, roles[i - 1].position)
  }

  const move = (id: string, direction: 'up' | 'down') => {
    setMoving(true)
    setMoveError(null)
    api
      .moveRole(id, direction)
      .then((r) => setRoles(r.roles))
      .catch((e: unknown) => setMoveError(e instanceof ApiError ? e.message : 'Could not move the role.'))
      .finally(() => setMoving(false))
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-end gap-3">
        <ErrorText>{moveError}</ErrorText>
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

        {roles.map((r, i) => (
          <RoleEditor
            key={r.id}
            role={r}
            catalogue={catalogue}
            me={me}
            onSaved={() => void refresh()}
            mover={{
              canUp: canMove(i, 'up'),
              canDown: canMove(i, 'down'),
              busy: moving,
              onMove: (direction) => move(r.id, direction),
            }}
          />
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
  mover,
}: {
  role?: RoleView
  catalogue: PermissionInfo[]
  me: CurrentUser
  onSaved: () => void
  onCancel?: () => void
  mover?: { canUp: boolean; canDown: boolean; busy: boolean; onMove: (direction: 'up' | 'down') => void }
}) {
  const [name, setName] = useState(role?.name ?? '')
  const [description, setDescription] = useState(role?.description ?? '')
  const [permissions, setPermissions] = useState<string[]>(role?.permissionNames ?? [])
  const [open, setOpen] = useState(role === undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const isAdmin = me.permissionNames.includes('Administrator')
  const locked = role?.locked ?? false
  // A role at or above your highest role is shown but not changed by you (design §3.5). A new one
  // is made at the bottom, so always yours to fill in.
  const outranked = role !== undefined && !isBelowMe(me, role.position)
  const outrankedWhy = outranked ? 'You can only change roles below your highest role.' : undefined
  const canTick = (p: PermissionInfo) => !outranked && (isAdmin || me.permissionNames.includes(p.name))

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
      <CardHeader className={cn('flex-nowrap gap-x-0 p-0', !open && 'border-b-0 bg-card')}>
        <button
          type="button"
          className="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 gap-y-0.5 px-(--panel-pad) py-1.5 text-left hover:bg-muted/50"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
        >
          {/* Wraps the counts onto their own line on a phone rather than squeezing the name and
              its line to a word wide: the move buttons take room beside it. */}
          <div className="min-w-0 grow basis-40">
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
          {role && (
            <span className="ml-auto shrink-0 text-right text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
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
        {mover && (
          <div className="flex shrink-0 items-center gap-1 pr-(--panel-pad)">
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              aria-label={`Move ${role?.name} up`}
              title={outranked ? outrankedWhy : undefined}
              disabled={!mover.canUp || mover.busy}
              onClick={() => mover.onMove('up')}
            >
              <ChevronUp />
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              aria-label={`Move ${role?.name} down`}
              title={outranked ? outrankedWhy : undefined}
              disabled={!mover.canDown || mover.busy}
              onClick={() => mover.onMove('down')}
            >
              <ChevronDown />
            </Button>
          </div>
        )}
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
                      disabled={role?.isBuiltIn || outranked}
                      title={role?.isBuiltIn ? 'Built-in roles keep their names.' : outrankedWhy}
                      onChange={(e) => setName(e.target.value)}
                    />
                  </Field>
                  <Field label="One line about it" htmlFor={`role-desc-${role?.id ?? 'new'}`}>
                    <Input
                      id={`role-desc-${role?.id ?? 'new'}`}
                      value={description}
                      disabled={outranked}
                      title={outrankedWhy}
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
                              title={
                                allowed
                                  ? p.description
                                  : (outrankedWhy ?? 'You can only put permissions into a role that you have yourself.')
                              }
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

                {role && can(me, 'ManageUsers') && <RoleDiscordRoles roleId={role.id} />}

                <ErrorText>{error}</ErrorText>

                <div className="flex items-center gap-2">
                  <Button
                    size="sm"
                    onClick={save}
                    disabled={busy || !dirty || !name.trim() || outranked}
                    title={outrankedWhy}
                  >
                    {busy ? 'Saving…' : role ? 'Save changes' : 'Create role'}
                  </Button>
                  {onCancel && (
                    <Button size="sm" variant="ghost" onClick={onCancel} disabled={busy}>
                      Cancel
                    </Button>
                  )}
                  <div className="flex-1" />
                  {role && !role.isBuiltIn && (
                    <ConfirmButton
                      size="sm"
                      variant="destructive"
                      onConfirm={remove}
                      disabled={busy || role.userCount > 0 || outranked}
                      title={
                        outranked
                          ? outrankedWhy
                          : role.userCount > 0
                            ? 'Move the people who hold it to another role first.'
                            : undefined
                      }
                    >
                      Delete role
                    </ConfirmButton>
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
