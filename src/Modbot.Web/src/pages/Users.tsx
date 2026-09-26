import { useCallback, useEffect, useMemo, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Checkbox } from '@/components/ui/checkbox'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Tabs } from '@/components/ui/tabs'
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
import { usernameProblem } from '@/lib/username'
import { cn } from '@/lib/utils'
import { Empty } from '@/pages/Members'
import { ErrorText, Field } from '@/pages/setup/WizardChrome'
import { Notice } from '@/components/ui/notice'

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

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!users) return <Empty>Loading…</Empty>

  return (
    <div className="flex flex-col gap-3">
      <div className="flex justify-end">
        <Button onClick={() => setAdding(true)}>
          Add someone
        </Button>
      </div>

      <Card>
        <Table
          head={
            <>
              <Th>Username</Th>
              <Th>VRChat account</Th>
              <Th>Roles</Th>
              <Th>Last sign-in</Th>
              <Th />
            </>
          }
        >
          {users.map((u) => (
            <Tr
              key={u.id}
              className={cn(!u.isDeleted && 'cursor-pointer hover:bg-muted/40', u.isDisabled && 'text-muted-foreground')}
              onClick={() => !u.isDeleted && setSelected(u.id)}
            >
              <Td className="font-medium">
                {u.username}
                {u.id === me.id && <span className="ml-2 text-muted-foreground">you</span>}
              </Td>
              <Td>
                {u.vrChatLinked ? (
                  <span title={u.vrChatUserId ?? undefined}>{u.vrChatDisplayName ?? u.vrChatUserId}</span>
                ) : (
                  <Badge variant="outline">Not linked yet</Badge>
                )}
              </Td>
              <Td>
                <div className="flex flex-wrap gap-1">
                  {u.roles.map((r) => (
                    <Badge key={r.id} variant="secondary">
                      {r.name}
                    </Badge>
                  ))}
                  {u.roles.length === 0 && <span className="text-muted-foreground">No roles</span>}
                </div>
              </Td>
              <Td className={cn(u.lastLoginAt && 'font-mono')}>{u.lastLoginAt ? formatDay(u.lastLoginAt) : 'Never'}</Td>
              <Td className="text-right">
                {u.isDeleted ? (
                  <Badge variant="destructive">Deleted</Badge>
                ) : (
                  u.isDisabled && <Badge variant="destructive">Disabled</Badge>
                )}
              </Td>
            </Tr>
          ))}
        </Table>
      </Card>

      {invites.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Invite links not used yet</CardTitle>
          </CardHeader>
          <div className="divide-y-(--hairline) divide-border" style={{ fontSize: 'var(--text-small)' }}>
            {invites.map((i) => (
              <div
                key={i.id}
                className="flex flex-wrap items-center gap-x-3 gap-y-1 px-(--panel-pad) py-1"
                style={{ minHeight: 'var(--row-h)' }}
              >
                <span>
                  Made by <span className="font-medium">{i.createdBy}</span> for{' '}
                  {i.roles.length ? i.roles.join(', ') : 'no roles'}
                </span>
                <span className="text-muted-foreground">
                  expires <span className="font-mono">{formatDay(i.expiresAt)}</span>
                </span>
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

    if (way === 'password') {
      const problem = usernameProblem(username)
      if (problem) {
        setError(problem)
        setBusy(false)
        return
      }
    }

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
        <Notice tone="ok" title="Invite link made.">
          It will not be shown again.
        </Notice>
        <CopyBox text={fullUrl(made)} />
      </div>
    )
  }

  if (created) {
    return <Notice tone="ok" title={`${created} can sign in now.`} />
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <Tabs
        value={way}
        onChange={setWay}
        tabs={[
          { value: 'link', label: 'Send them a link' },
          { value: 'password', label: 'Set a temporary password' },
        ]}
      >
        {way === 'password' && (
          <div className="space-y-3 pt-4">
            <Field label="Username" htmlFor="new-username">
              <Input id="new-username" required autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />
            </Field>
            <Field label="Email" htmlFor="new-email">
              <Input id="new-email" type="email" required autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} />
            </Field>
            <div className="grid items-end gap-3 sm:grid-cols-2">
              <Field label="Temporary password" hint="at least 12 characters" htmlFor="new-password">
                <Input id="new-password" type="password" required minLength={12} autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
              </Field>
              <Field label="Confirm" htmlFor="new-confirm">
                <Input id="new-confirm" type="password" required autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
              </Field>
            </div>
          </div>
        )}
      </Tabs>

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
            <Checkbox
              key={r.id}
              disabled={!allowed}
              title={allowed ? undefined : 'You can only give people permissions you have yourself.'}
              checked={value.includes(r.id)}
              onChange={(on) => onChange(on ? [...value, r.id] : value.filter((id) => id !== r.id))}
            >
              <span className="font-medium">{r.name}</span>
              {r.description && <span className="text-muted-foreground"> · {r.description}</span>}
            </Checkbox>
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
  const [deleting, setDeleting] = useState(false)

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

  const contactChanged = email !== (user.email ?? '') || discord !== (user.discordUserId ?? '')

  return (
    <aside
      className="fixed inset-y-0 right-0 z-30 flex w-[26rem] max-w-full flex-col overflow-auto border-l-(length:--hairline) bg-card shadow-sm"
      aria-label={`Account: ${user.username}`}
    >
      {/* The sheet scrolls as one column, so the strip must not be squeezed to its minimum. */}
      <CardHeader className="shrink-0 flex-nowrap">
        <div className="min-w-0">
          <CardTitle>{user.username}</CardTitle>
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {user.vrChatLinked ? (
              <>
                VRChat:{user.vrChatDisplayName && ` ${user.vrChatDisplayName}`}
                {user.vrChatUserId && (
                  <>
                    {' '}
                    <span className="font-mono">{user.vrChatUserId}</span>
                  </>
                )}
              </>
            ) : (
              'Has not linked a VRChat account yet'
            )}
          </div>
        </div>
        <CardAction>
          {user.isDisabled && <Badge variant="destructive">Disabled</Badge>}
          <Button variant="ghost" size="xs" onClick={onClose}>
            Close
          </Button>
        </CardAction>
      </CardHeader>

      <div className="flex flex-col gap-5 p-(--panel-pad)" style={{ fontSize: 'var(--text-small)' }}>
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
          <div className="font-label">Contact details</div>
          <Field label="Email" htmlFor={`email-${user.id}`}>
            <Input id={`email-${user.id}`} type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <Field label="Discord user id" htmlFor={`discord-${user.id}`}>
            <Input id={`discord-${user.id}`} className="font-mono" value={discord} onChange={(e) => setDiscord(e.target.value)} />
          </Field>
          <Button
            size="sm"
            disabled={!contactChanged || busy !== null}
            onClick={() => run('contact', () => api.setUserContact(user.id, { email, discordUserId: discord }))}
          >
            {busy === 'contact' ? 'Saving…' : 'Save contact details'}
          </Button>
        </section>

        <section className="space-y-2">
          <div className="font-label">Password</div>
          {resetLink ? (
            <>
              <Notice tone="ok" title="Reset link made." />
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
          <div className="font-label">Access</div>
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
          <Button
            variant="destructive"
            size="sm"
            disabled={busy !== null || user.id === me.id}
            title={user.id === me.id ? 'You cannot delete your own account.' : undefined}
            onClick={() => setDeleting(true)}
          >
            Delete this account
          </Button>
        </section>

        <ErrorText>{error}</ErrorText>
      </div>

      <Dialog open={deleting} onOpenChange={setDeleting}>
        <DialogContent title={`Delete ${user.username}`}>
          <DeleteAccount
            user={user}
            onDone={() => {
              setDeleting(false)
              onClose()
              onChanged()
            }}
          />
        </DialogContent>
      </Dialog>
    </aside>
  )
}

/**
 * Deleting is not undoable, so it asks for the username in full rather than for a click on a
 * second button: typing a name is a thing you cannot do by accident on the wrong row.
 *
 * The server checks the typed name too — this only stops the button being pressed.
 */
function DeleteAccount({ user, onDone }: { user: UserSummary; onDone: () => void }) {
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const matches = typed.trim().toLocaleUpperCase() === user.username.toLocaleUpperCase()

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    api
      .deleteUser(user.id, typed)
      .then(onDone)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not delete the account.'))
      .finally(() => setBusy(false))
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label={`Type ${user.username} to confirm`} htmlFor={`delete-${user.id}`}>
        <Input
          id={`delete-${user.id}`}
          autoComplete="off"
          autoFocus
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
        />
      </Field>

      <ErrorText>{error}</ErrorText>

      <div className="flex justify-end">
        <Button type="submit" variant="destructive" size="sm" disabled={!matches || busy}>
          {busy ? 'Deleting…' : 'Delete this account'}
        </Button>
      </div>
    </form>
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
