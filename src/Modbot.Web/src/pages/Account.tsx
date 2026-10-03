import { useEffect, useRef, useState } from 'react'
import { ChevronRight, Rows2, Rows3 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { Button, buttonVariants } from '@/components/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { PanelGrid } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { NotificationChoicesCard } from '@/components/account/NotificationChoicesCard'
import { VRChatLinkPanel } from '@/components/VRChatLinkPanel'
import { ApiError, api, type CurrentUser, type DiscordCodeStatus } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { registerLink } from '@/lib/myModbot'
import { useQueryParam } from '@/lib/router'
import { usernameProblem } from '@/lib/username'
import { ErrorText, Field } from '@/pages/setup/WizardChrome'
import { Notice } from '@/components/ui/notice'
import { SwitchBank } from '@/components/ui/switch-bank'
import type { Density } from '@/lib/preferences'

/**
 * The signed-in person's own account (accounts and access design §4, §8): username, password,
 * where a reset link can reach them, the linked VRChat and Discord accounts, and the way out of
 * every session.
 *
 * Also a desk's spacing, which was a three-way switch in the top bar until the headset became a
 * place of its own (UX review 2026-09-25, finding 17): people set it once, and it is kept per
 * browser like the theme, not on the account.
 */
export function Account({
  me,
  onChanged,
  density,
  setDensity,
}: {
  me: CurrentUser
  onChanged: () => void
  density: Density
  setDensity: (d: Density) => void
}) {
  return (
    <PanelGrid className="lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>You</CardTitle>
        </CardHeader>
        <CardContent>
          <div style={{ fontSize: 'var(--text-small)' }}>
            <div>
              Signed in as <span className="font-medium">{me.username}</span>
            </div>
            <div className="mt-1 flex flex-wrap gap-1">
              {me.roles.map((r) => (
                <Badge key={r} variant="secondary">
                  {r}
                </Badge>
              ))}
              {me.roles.length === 0 && <span className="text-muted-foreground">No roles yet</span>}
            </div>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Spacing at a desk</CardTitle>
        </CardHeader>
        <CardContent>
          <SwitchBank
            label="Spacing at a desk"
            value={density}
            onChange={setDensity}
            options={[
              { value: 'dense', label: 'Dense', icon: <Rows3 className="size-3.5" /> },
              { value: 'comfortable', label: 'Comfortable', icon: <Rows2 className="size-3.5" /> },
            ]}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Your VRChat account</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div style={{ fontSize: 'var(--text-small)' }}>
            {me.vrChatLinked ? (
              <>
                Linked to <span className="font-medium">{me.vrChatDisplayName ?? me.vrChatUserId}</span>{' '}
                <span className="font-mono text-muted-foreground">{me.vrChatUserId}</span>
              </>
            ) : (
              'Not linked yet.'
            )}
          </div>
          <details className="group">
            <summary
              className="flex w-fit cursor-pointer list-none items-center gap-1 rounded-sm text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden"
              style={{ fontSize: 'var(--text-small)' }}
            >
              <ChevronRight
                className="size-3.5 shrink-0 transition-transform group-open:rotate-90 motion-reduce:transition-none"
                aria-hidden
              />
              Link a different account
            </summary>
            <div className="mt-3">
              <VRChatLinkPanel compact onLinked={onChanged} />
            </div>
          </details>
        </CardContent>
      </Card>

      <YourDiscord me={me} onChanged={onChanged} />

      <ChangeUsername me={me} onChanged={onChanged} />
      <Contact me={me} onChanged={onChanged} />
      <ChangePassword />
      <NotificationChoicesCard />
      <EventInvites me={me} onChanged={onChanged} />

      <Card>
        <CardHeader>
          <CardTitle>my.modbot.co</CardTitle>
        </CardHeader>
        <CardContent>
          <a
            href={registerLink()}
            target="_blank"
            rel="noopener noreferrer"
            className={buttonVariants({ variant: 'outline', size: 'sm' })}
          >
            Add to my.modbot.co
          </a>
        </CardContent>
      </Card>

      <SignOutEverywhere />
    </PanelGrid>
  )
}

/**
 * Whether events that name this account as host or staff invite it when their instance opens
 * (calendar auto-invite design §2.1). On unless turned off here.
 */
function EventInvites({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const change = (on: boolean) => {
    setBusy(true)
    setError(null)
    api
      .setEventInvites(on)
      .then(onChanged)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Event invites</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-2">
        <Checkbox checked={me.getsEventInvites ?? true} disabled={busy} onChange={change}>
          Get event invites
        </Checkbox>
        <ErrorText>{error}</ErrorText>
      </CardContent>
    </Card>
  )
}

function ChangeUsername({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  const [username, setUsername] = useState(me.username)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    const problem = usernameProblem(username)
    if (problem) {
      setError(problem)
      return
    }
    setBusy(true)
    setDone(false)
    setError(null)
    api
      .changeUsername({ username, currentPassword: password })
      .then(() => {
        setDone(true)
        setPassword('')
        onChanged()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not change your username.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Change your username</CardTitle>
      </CardHeader>
      <form onSubmit={submit} className="flex flex-1 flex-col">
        <CardContent className="flex flex-1 flex-col gap-3">
          <Field label="New username" htmlFor="acct-username">
            <Input id="acct-username" required autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} />
          </Field>
          <Field label="Your current password" htmlFor="acct-username-pw">
            <Input id="acct-username-pw" type="password" required autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          <ErrorText>{error}</ErrorText>
        </CardContent>
        <CardFooter className="flex-wrap gap-3">
          <Button type="submit" size="xs" disabled={busy || username.trim() === me.username || !password}>
            {busy ? 'Saving…' : 'Change username'}
          </Button>
          {done && <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>Changed.</span>}
        </CardFooter>
      </form>
    </Card>
  )
}

function ChangePassword() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    if (next !== confirm) {
      setError('The passwords do not match.')
      return
    }
    setBusy(true)
    setDone(false)
    setError(null)
    api
      .changePassword({ currentPassword: current, newPassword: next, confirmPassword: confirm })
      .then(() => {
        setDone(true)
        setCurrent('')
        setNext('')
        setConfirm('')
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not change your password.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Change your password</CardTitle>
      </CardHeader>
      <form onSubmit={submit} className="flex flex-1 flex-col">
        <CardContent className="flex flex-1 flex-col gap-3">
          <Field label="Current password" htmlFor="acct-pw-current">
            <Input id="acct-pw-current" type="password" required autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
          <div className="grid grid-cols-2 items-end gap-3">
            <Field label="New password" hint="at least 12 characters" htmlFor="acct-pw-new">
              <Input id="acct-pw-new" type="password" required minLength={12} autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
            </Field>
            <Field label="Confirm" htmlFor="acct-pw-confirm">
              <Input id="acct-pw-confirm" type="password" required autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
            </Field>
          </div>
          <ErrorText>{error}</ErrorText>
        </CardContent>
        <CardFooter className="flex-wrap gap-3">
          <Button type="submit" size="xs" disabled={busy}>
            {busy ? 'Saving…' : 'Change password'}
          </Button>
          {done && (
            <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>
              Changed. Other sessions signed out.
            </span>
          )}
        </CardFooter>
      </form>
    </Card>
  )
}

/** What the account page says when Discord sends the browser back (`?discord=`), when it is not good news. */
const DISCORD_PROBLEMS: Record<string, string> = {
  'not-set-up': 'Discord sign-in is not set up on this server.',
  taken: 'That Discord account is connected to another Modbot account.',
  cancelled: 'Discord sign-in was cancelled.',
  'sign-in-expired': 'The Discord sign-in took too long. Try again.',
  discord: 'Discord did not say who signed in. Try again.',
  'signed-out': 'You were signed out of Modbot before Discord sent you back.',
}

/** How often the card looks for `/verify` having used the code it shows. */
const CODE_POLL_MS = 5000

/**
 * The Discord account the bot treats as this person (accounts and access design §4.6). Proven by
 * signing in to Discord: the button is a link to the server, which sends the browser to Discord
 * and back here with `?discord=` saying how it went. Or proven with a code: the card shows one,
 * the person runs `/verify` with it in the Discord server, and Discord says who ran it (Discord
 * account linking design §14). An id typed in before proving existed shows as not proven, with the
 * day it stops counting.
 *
 * Connect Discord is offered where sign-in is set up, the code where the bot is; where neither is,
 * Connect Discord stays, so pressing it says what is missing. While a code is up the card asks
 * every few seconds whether the account is connected yet, so it changes by itself once `/verify`
 * worked, and drops the code once it runs out.
 */
function YourDiscord({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  const [result, setResult] = useQueryParam('discord')
  const [problem] = useState(() => (result ? (DISCORD_PROBLEMS[result] ?? null) : null))
  const [error, setError] = useState<string | null>(problem)
  const [busy, setBusy] = useState(false)
  const [ways, setWays] = useState<DiscordCodeStatus | null>(null)
  const [codeBusy, setCodeBusy] = useState(false)
  const [copied, setCopied] = useState(false)
  const [now, setNow] = useState(() => Date.now())

  // Read once, then off the address, so a reload or a copied link does not say it again.
  useEffect(() => {
    if (result !== null) setResult(null)
  }, [result, setResult])

  // What this server offers, and a code still live from before a reload.
  useEffect(() => {
    let current = true
    api
      .discordCode()
      .then((status) => {
        if (current) setWays(status)
      })
      .catch(() => undefined)
    return () => {
      current = false
    }
  }, [])

  const code =
    !me.discordProven && ways?.code && ways.expiresAt && new Date(ways.expiresAt).getTime() > now ? ways.code : null

  const changed = useRef(onChanged)
  useEffect(() => {
    changed.current = onChanged
  }, [onChanged])

  useEffect(() => {
    if (!code) return
    const timer = window.setInterval(() => {
      setNow(Date.now())
      void api
        .me()
        .then((fresh) => {
          if (fresh.discordProven) changed.current()
        })
        .catch(() => undefined)
    }, CODE_POLL_MS)
    return () => window.clearInterval(timer)
  }, [code])

  useEffect(() => {
    if (!copied) return
    const timer = window.setTimeout(() => setCopied(false), 1500)
    return () => window.clearTimeout(timer)
  }, [copied])

  const newCode = () => {
    setCodeBusy(true)
    setError(null)
    api
      .newDiscordCode()
      .then((status) => {
        setWays(status)
        setNow(Date.now())
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not make a code.'))
      .finally(() => setCodeBusy(false))
  }

  const copy = (text: string) => {
    void navigator.clipboard?.writeText(text).then(() => setCopied(true))
  }

  const offerSignIn = !me.discordProven && (ways === null || ways.signInSetUp || !ways.commandSetUp)
  const offerCode = !me.discordProven && ways?.commandSetUp === true

  const disconnect = () => {
    setBusy(true)
    setError(null)
    api
      .disconnectDiscord()
      .then(() => {
        onChanged()
        // A code shown before it was used is gone on the server; ask again rather than show it.
        return api.discordCode().then(setWays)
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not disconnect.'))
      .finally(() => setBusy(false))
  }

  const typedUntil = !me.discordProven ? me.discordWorksUntil : null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Your Discord account</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-1 flex-col gap-3">
        <div style={{ fontSize: 'var(--text-small)' }}>
          {me.discordUserId && me.discordProven ? (
            <>
              Connected as <span className="font-medium">{me.discordUsername ?? me.discordUserId}</span>{' '}
              <span className="font-mono text-muted-foreground">{me.discordUserId}</span>
            </>
          ) : me.discordUserId ? (
            <>
              <span className="font-mono">{me.discordUserId}</span>{' '}
              <Badge variant="warn">Not proven</Badge>
              {typedUntil && (
                <div className="mt-1 text-muted-foreground">
                  {new Date(typedUntil) > new Date() ? 'Works until ' : 'Stopped working '}
                  {formatDay(typedUntil, true)}
                </div>
              )}
            </>
          ) : (
            'Not connected.'
          )}
        </div>
        {code && (
          <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Code</span>
            <div className="flex items-center gap-2">
              <code
                className="rounded-sm border-(length:--hairline) bg-strip px-3 py-1.5 font-mono font-semibold select-all"
                style={{ fontSize: 'calc(var(--text-base) + 2px)' }}
              >
                {code}
              </code>
              <Button type="button" variant="outline" size="sm" onClick={() => copy(code)}>
                {copied ? 'Copied' : 'Copy'}
              </Button>
            </div>
            <code className="w-fit rounded-sm border-(length:--hairline) bg-strip px-2 py-0.5 font-mono select-all">
              /verify {code}
            </code>
          </div>
        )}
        <ErrorText>{error}</ErrorText>
      </CardContent>
      <CardFooter className="flex-wrap gap-3">
        {offerSignIn && (
          <a href={api.connectDiscordUrl} className={buttonVariants({ size: 'xs' })}>
            Connect Discord
          </a>
        )}
        {offerCode && (
          <Button size="xs" variant={offerSignIn ? 'outline' : 'default'} disabled={codeBusy} onClick={newCode}>
            {codeBusy ? 'Making a code…' : code ? 'New code' : 'Connect with a code'}
          </Button>
        )}
        {me.discordUserId && (
          <Button size="xs" variant="outline" disabled={busy} onClick={disconnect}>
            {busy ? 'Disconnecting…' : me.discordProven ? 'Disconnect' : 'Remove'}
          </Button>
        )}
      </CardFooter>
    </Card>
  )
}

/**
 * Email. Required now (server info and account email design §4): an account made before that rule
 * lands here with the field empty, which is how such a person is asked for one.
 */
function Contact({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  const [email, setEmail] = useState(me.email ?? '')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setDone(false)
    setError(null)
    api
      .setOwnContact({ email })
      .then(() => {
        setDone(true)
        onChanged()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>How you can be reached</CardTitle>
      </CardHeader>
      <form onSubmit={submit} className="flex flex-1 flex-col">
        <CardContent className="flex flex-1 flex-col gap-3">
          <Field label="Email" htmlFor="acct-email">
            <Input id="acct-email" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <ErrorText>{error}</ErrorText>
        </CardContent>
        <CardFooter className="flex-wrap gap-3">
          <Button type="submit" size="xs" disabled={busy}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          {done && <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>Saved.</span>}
        </CardFooter>
      </form>
    </Card>
  )
}

function SignOutEverywhere() {
  const [busy, setBusy] = useState(false)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Sign out everywhere</CardTitle>
      </CardHeader>
      <Notice>Includes this browser.</Notice>
      <CardFooter>
        <Button
          size="xs"
          variant="destructive"
          disabled={busy}
          onClick={() => {
            setBusy(true)
            void api.signOutEverywhere().finally(() => window.location.assign('/'))
          }}
        >
          {busy ? 'Signing out…' : 'Sign out everywhere'}
        </Button>
      </CardFooter>
    </Card>
  )
}
