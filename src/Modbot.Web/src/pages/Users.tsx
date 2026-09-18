import { useCallback, useEffect, useMemo, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  ApiError,
  api,
  type CurrentUser,
  type LinkCreated,
  type PendingInvite,
  type RoleView,
  type UserSummary,
} from '@/lib/api'
import { formatDay } from '@/lib/format'
import { cn } from '@/lib/utils'
import { ErrorText, Field, Note } from '@/pages/setup/WizardChrome'

/**
 * The users page (accounts and access design §4, §8).
 *
 * Adding somebody offers the invite link first and the temporary password second, on purpose:
 * an account whose password two people know is an account two people can act as, and the whole
 * point of accounts is knowing who did what.
 */
export function Users({ me }: { me: CurrentUser }) {
  const [users, setUsers] = useState<UserSummary[] | null>(null)
  const [roles, setRoles] = useState<RoleView[]>([])
  const [invites, setInvites] = useState<PendingInvite[]>([])
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)

  const refresh = useCallback(
    () =>
      Promise.all([api.users(), api.roles(), api.invites()])
        .then(([u, r, i]) => {
          setUsers(u)
          setRoles(r.roles)
          setInvites(i)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load users.')),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  const current = useMemo(() => users?.find((u) => u.id === selected) ?? null, [users, selected])

  if (error) return <Empty>{error}</Empty>
  if (!users) return <Empty>Loading…</Empty>

  return (
    <div className="flex flex-col gap-4">
      <div className="flex justify-end">
        <Button size="sm" onClick={() => setAdding(true)}>
          Add someone
        </Button>
      </div>

      <Card className="py-0">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Username</TableHead>
              <TableHead>VRChat account</TableHead>
              <TableHead>Roles</TableHead>
              <TableHead>Last sign-in</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {users.map((u) => (
              <TableRow
                key={u.id}
                className={cn('cursor-pointer', u.isDisabled && 'text-muted-foreground')}
                onClick={() => setSelected(u.id)}
              >
                <TableCell className="font-medium">
                  {u.username}
                  {u.id === me.id && (
                    <span className="ml-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                      you
                    </span>
                  )}
                </TableCell>
                <TableCell>
                  {u.vrChatLinked ? (
                    <span title={u.vrChatUserId ?? undefined}>{u.vrChatDisplayName ?? u.vrChatUserId}</span>
                  ) : (
                    <Badge variant="outline">Not linked yet</Badge>
                  )}
                </TableCell>
                <TableCell>
                  <div className="flex flex-wrap gap-1">
                    {u.roles.map((r) => (
                      <Badge key={r.id} variant="secondary">
                        {r.name}
                      </Badge>
                    ))}
                    {u.roles.length === 0 && <span className="text-muted-foreground">No roles</span>}
                  </div>
                </TableCell>
                <TableCell>{u.lastLoginAt ? formatDay(u.lastLoginAt) : 'Never'}</TableCell>
                <TableCell className="text-right">
                  {u.isDisabled && <Badge variant="destructive">Disabled</Badge>}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      {invites.length > 0 && (
        <Card>
          <CardContent>
            <div className="mb-2 font-semibold">Invite links not used yet</div>
            <div className="divide-y" style={{ fontSize: 'var(--text-small)' }}>
              {invites.map((i) => (
                <div key={i.id} className="flex items-center gap-3 py-2">
                  <span>
                    Made by <span className="font-medium">{i.createdBy}</span> for{' '}
                    {i.roles.length ? i.roles.join(', ') : 'no roles'}
                  </span>
                  <span className="text-muted-foreground">expires {formatDay(i.expiresAt)}</span>
                  <div className="flex-1" />
                  <Button
                    variant="ghost"
                    size="xs"
                    onClick={() => void api.revokeInvite(i.id).then(refresh)}
                  >
                    Take back
                  </Button>
                </div>
              ))}
            </div>
          </CardContent>
        </Card>
      )}

      <Dialog open={adding} onOpenChange={setAdding}>
        <DialogContent title="Add someone">
          <AddSomeone roles={roles} me={me} onDone={() => void refresh()} />
        </DialogContent>
      </Dialog>

      {current && (
        <UserDrawer
          key={current.id}
          user={current}
          roles={roles}
          me={me}
          onClose={() => setSelected(null)}
          onChanged={() => void refresh()}
        />
      )}
    </div>
  )
}

/** The full address for a link the server returned, built here when no public address is saved. */
function fullUrl(link: LinkCreated): string {
  return link.url ?? window.location.origin + link.path
}

function AddSomeone({
  roles,
  me,
  onDone,
}: {
  roles: RoleView[]
  me: CurrentUser
  onDone: () => void
}) {
  const [way, setWay] = useState<'link' | 'password'>('link')
  const [roleIds, setRoleIds] = useState<string[]>([])
  const [username, setUsername] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [made, setMade] = useState<LinkCreated | null>(null)
  const [created, setCreated] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)

    const action =
      way === 'link'
        ? api.createInvite(roleIds).then((link) => {
            setMade(link)
            onDone()
          })
        : api
            .createUser({ username, password, confirmPassword: confirm, roleIds, email })
            .then((u) => {
              setCreated(u.username)
              onDone()
            })

    action
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not add them.'))
      .finally(() => setBusy(false))
  }

  if (made) {
    return (
      <div className="space-y-3">
        <Note tone="ok" title="Invite link made.">
          It will not be shown again.
        </Note>
        <CopyBox text={fullUrl(made)} />
      </div>
    )
  }

  if (created) {
    return <Note tone="ok" title={`${created} can sign in now.`} />
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <div role="tablist" className="flex gap-1 rounded-md border bg-secondary p-0.5" style={{ fontSize: 'var(--text-small)' }}>
        {(
          [
            ['link', 'Send them a link'],
            ['password', 'Set a temporary password'],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={way === id}
            onClick={() => setWay(id)}
            className={cn(
              'flex-1 rounded-md px-2 py-1 font-medium transition-colors',
              way === id ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {label}
          </button>
        ))}
      </div>

      {way === 'password' && (
        <div className="space-y-3">
          <Field label="Username" htmlFor="new-username">
            <Input id="new-username" required autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />
          </Field>
          <Field label="Email" htmlFor="new-email">
            <Input id="new-email" type="email" required autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Temporary password" hint="at least 12 characters" htmlFor="new-password">
              <Input id="new-password" type="password" required minLength={12} autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </Field>
            <Field label="Confirm" htmlFor="new-confirm">
              <Input id="new-confirm" type="password" required autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
            </Field>
          </div>
        </div>
      )}

      <RolePicker roles={roles} me={me} value={roleIds} onChange={setRoleIds} />

      <ErrorText>{error}</ErrorText>

      <div className="flex justify-end">
        <Button type="submit" size="sm" disabled={busy}>
          {busy ? 'Working…' : way === 'link' ? 'Make the link' : 'Create the account'}
        </Button>
      </div>
    </form>
  )
}

/**
 * Which roles to give. Roles the signed-in person could not hand out — ones that allow something
 * they cannot do themselves — are shown but cannot be ticked, and say why.
 */
function RolePicker({
  roles,
  me,
  value,
  onChange,
}: {
  roles: RoleView[]
  me: CurrentUser
  value: string[]
  onChange: (next: string[]) => void
}) {
  const isAdmin = me.permissionNames.includes('Administrator')
  const canGive = (r: RoleView) => isAdmin || r.permissionNames.every((p) => me.permissionNames.includes(p))

  return (
    <div>
      <div className="mb-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Roles
      </div>
      <div className="space-y-1">
        {roles.map((r) => {
          const allowed = canGive(r)
          return (
            <label
              key={r.id}
              className={cn('flex items-start gap-2 rounded-md px-2 py-1', !allowed && 'opacity-60')}
              style={{ fontSize: 'var(--text-small)' }}
              title={allowed ? undefined : 'You can only give people permissions you have yourself.'}
            >
              <input
                type="checkbox"
                className="mt-0.5"
                disabled={!allowed}
                checked={value.includes(r.id)}
                onChange={(e) =>
                  onChange(e.target.checked ? [...value, r.id] : value.filter((id) => id !== r.id))
                }
              />
              <span>
                <span className="font-medium">{r.name}</span>
                {r.description && <span className="text-muted-foreground"> · {r.description}</span>}
              </span>
            </label>
          )
        })}
      </div>
    </div>
  )
}

function UserDrawer({
  user,
  roles,
  me,
  onClose,
  onChanged,
}: {
  user: UserSummary
  roles: RoleView[]
  me: CurrentUser
  onClose: () => void
  onChanged: () => void
}) {
  const [roleIds, setRoleIds] = useState(user.roles.map((r) => r.id))
  const [email, setEmail] = useState(user.email ?? '')
  const [discord, setDiscord] = useState(user.discordUserId ?? '')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [resetLink, setResetLink] = useState<LinkCreated | null>(null)

  const run = (name: string, action: () => Promise<unknown>) => {
    setBusy(name)
    setError(null)
    action()
      .then(onChanged)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'That did not work.'))
      .finally(() => setBusy(null))
  }

  const rolesChanged =
    roleIds.length !== user.roles.length || roleIds.some((id) => !user.roles.some((r) => r.id === id))

  return (
    <aside
      className="fixed inset-y-0 right-0 z-30 flex w-[26rem] max-w-full flex-col overflow-auto border-l bg-card shadow-xl"
      style={{ borderLeftWidth: 'var(--hairline)' }}
      aria-label={`Account: ${user.username}`}
    >
      <div className="flex items-center gap-2 border-b px-5 py-3" style={{ borderBottomWidth: 'var(--hairline)' }}>
        <div>
          <div className="font-semibold">{user.username}</div>
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {user.vrChatLinked
              ? `VRChat: ${user.vrChatDisplayName ?? ''} ${user.vrChatUserId ?? ''}`.trim()
              : 'Has not linked a VRChat account yet'}
          </div>
        </div>
        <div className="flex-1" />
        {user.isDisabled && <Badge variant="destructive">Disabled</Badge>}
        <Button variant="ghost" size="sm" onClick={onClose}>
          Close
        </Button>
      </div>

      <div className="flex flex-col gap-5 p-5" style={{ fontSize: 'var(--text-small)' }}>
        <section className="space-y-2">
          <RolePicker roles={roles} me={me} value={roleIds} onChange={setRoleIds} />
          <Button
            size="sm"
            disabled={!rolesChanged || busy !== null}
            onClick={() => run('roles', () => api.setUserRoles(user.id, roleIds))}
          >
            {busy === 'roles' ? 'Saving…' : 'Save roles'}
          </Button>
        </section>

        <section className="space-y-2">
          <div className="font-semibold">Contact details</div>
          <Field label="Email" htmlFor={`email-${user.id}`}>
            <Input id={`email-${user.id}`} type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <Field label="Discord user id" htmlFor={`discord-${user.id}`}>
            <Input id={`discord-${user.id}`} className="font-mono" value={discord} onChange={(e) => setDiscord(e.target.value)} />
          </Field>
          <Button
            size="sm"
            variant="outline"
            disabled={busy !== null}
            onClick={() => run('contact', () => api.setUserContact(user.id, { email, discordUserId: discord }))}
          >
            {busy === 'contact' ? 'Saving…' : 'Save contact details'}
          </Button>
        </section>

        <section className="space-y-2">
          <div className="font-semibold">Password</div>
          {resetLink ? (
            <>
              <Note tone="ok" title="Reset link made." />
              <CopyBox text={fullUrl(resetLink)} />
            </>
          ) : (
            <Button
              size="sm"
              variant="outline"
              disabled={busy !== null || user.isDisabled}
              onClick={() =>
                run('reset', () => api.createResetLink(user.id).then((link) => setResetLink(link)))
              }
            >
              {busy === 'reset' ? 'Making…' : 'Make a reset link'}
            </Button>
          )}
        </section>

        <section className="space-y-2">
          <div className="font-semibold">Access</div>
          {user.isDisabled ? (
            <Button size="sm" disabled={busy !== null} onClick={() => run('enable', () => api.enableUser(user.id))}>
              {busy === 'enable' ? 'Working…' : 'Enable this account'}
            </Button>
          ) : (
            <Button
              size="sm"
              variant="destructive"
              disabled={busy !== null || user.id === me.id}
              title={user.id === me.id ? 'You cannot disable your own account.' : undefined}
              onClick={() => run('disable', () => api.disableUser(user.id))}
            >
              {busy === 'disable' ? 'Working…' : 'Disable this account'}
            </Button>
          )}
          {!user.isDisabled && <p className="text-muted-foreground">Disabling ends their sessions straight away.</p>}
        </section>

        <ErrorText>{error}</ErrorText>
      </div>
    </aside>
  )
}

export function CopyBox({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)

  return (
    <div className="flex items-center gap-2">
      <Input readOnly value={text} className="font-mono" onFocus={(e) => e.currentTarget.select()} />
      <Button
        type="button"
        size="sm"
        variant="outline"
        onClick={() =>
          void navigator.clipboard?.writeText(text).then(() => {
            setCopied(true)
            setTimeout(() => setCopied(false), 1500)
          })
        }
      >
        {copied ? 'Copied' : 'Copy'}
      </Button>
    </div>
  )
}

function Empty({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
    </Card>
  )
}
