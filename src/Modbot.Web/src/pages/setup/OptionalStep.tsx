import { useState } from 'react'
import { Input } from '@/components/ui/input'
import { ApiError, api } from '@/lib/api'
import { openRegisterOnce } from '@/lib/myModbot'
import { ErrorText, Field, WizardBody, WizardHeader } from './WizardChrome'
import { WIZARD_FORM_ID, type StepProps } from './types'

/**
 * Spec 7.1 step 5. Skippable means skippable.
 *
 * A deployment with neither a Discord bot nor SMTP is a complete, working Modbot: the bot does
 * not start and notifications degrade to the surfaces that remain. Nothing on this screen is
 * required, and the footer offers a genuine way past it.
 */
export function OptionalStep({ eyebrow, status, run, refresh }: StepProps) {
  const [botToken, setBotToken] = useState('')
  const [guildId, setGuildId] = useState(status.integrations.discordGuildId ?? '')
  const [smtpHost, setSmtpHost] = useState(status.integrations.smtpHost ?? '')
  const [smtpPort, setSmtpPort] = useState('')
  const [smtpUsername, setSmtpUsername] = useState('')
  const [smtpPassword, setSmtpPassword] = useState('')
  const [smtpFrom, setSmtpFrom] = useState('')
  const [error, setError] = useState<string | null>(null)

  // Prefilled from what the platform says, then from the address this page was opened at -- and
  // saved only when a person presses Continue with it on screen. The server never adopts either
  // on its own: this is the one thing a reset link is ever built from (design §4.2).
  const [publicAddress, setPublicAddress] = useState(
    status.integrations.publicAddress ??
      status.integrations.publicAddressSuggestion ??
      window.location.origin,
  )

  const submit = (event: React.FormEvent) => {
    event.preventDefault()

    // Straight from the submit, before anything is awaited, or the browser blocks the tab.
    if (!status.onboardingComplete) openRegisterOnce()

    run(async () => {
      setError(null)

      try {
        await api.saveIntegrations({
          // Fields are omitted rather than sent empty, because empty means "clear it" on the
          // server -- and a stored token the browser cannot read back would otherwise be wiped
          // by an operator who only came here to change the guild.
          discord: {
            ...(botToken ? { botToken } : {}),
            ...(guildId ? { guildId } : {}),
          },
          smtp: {
            ...(smtpHost ? { host: smtpHost } : {}),
            ...(smtpPort ? { port: Number(smtpPort) } : {}),
            ...(smtpUsername ? { username: smtpUsername } : {}),
            ...(smtpPassword ? { password: smtpPassword } : {}),
            ...(smtpFrom ? { fromAddress: smtpFrom } : {}),
          },
          ...(publicAddress.trim() ? { publicAddress: publicAddress.trim() } : {}),
        })

        await api.completeOnboarding()
        await refresh()
        return true
      } catch (e) {
        setError(e instanceof ApiError ? e.message : 'Could not save those settings.')
        return false
      }
    })
  }

  return (
    <form id={WIZARD_FORM_ID} onSubmit={submit}>
      <WizardHeader eyebrow={eyebrow} title="Public address, Discord and email" />
      <WizardBody>
        <div className="space-y-4">
          <div className="text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">
            Public address
          </div>
          <Field label="Address" htmlFor="public-address">
            <Input
              id="public-address"
              className="font-mono"
              autoComplete="off"
              spellCheck={false}
              placeholder="https://modbot.example.com"
              value={publicAddress}
              onChange={(e) => setPublicAddress(e.target.value)}
            />
          </Field>
        </div>

        <div className="space-y-4 pt-2">
          <div className="text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">
            Discord bot
          </div>
          <Field
            label="Bot token"
            hint={status.integrations.discordConfigured ? 'stored' : undefined}
            htmlFor="discord-token"
          >
            <Input
              id="discord-token"
              type="password"
              autoComplete="off"
              value={botToken}
              onChange={(e) => setBotToken(e.target.value)}
            />
          </Field>
          <Field label="Server ID" htmlFor="discord-guild">
            <Input
              id="discord-guild"
              className="font-mono"
              autoComplete="off"
              spellCheck={false}
              value={guildId}
              onChange={(e) => setGuildId(e.target.value)}
            />
          </Field>
        </div>

        <div className="space-y-4 pt-2">
          <div className="text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">
            Email (SMTP)
          </div>
          <div className="grid grid-cols-[1fr_6rem] gap-3">
            <Field label="Host" htmlFor="smtp-host">
              <Input
                id="smtp-host"
                autoComplete="off"
                spellCheck={false}
                placeholder="smtp.example.com"
                value={smtpHost}
                onChange={(e) => setSmtpHost(e.target.value)}
              />
            </Field>
            <Field label="Port" htmlFor="smtp-port">
              <Input
                id="smtp-port"
                inputMode="numeric"
                placeholder="587"
                value={smtpPort}
                onChange={(e) => setSmtpPort(e.target.value.replace(/\D/g, ''))}
              />
            </Field>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Username" htmlFor="smtp-username">
              <Input
                id="smtp-username"
                autoComplete="off"
                value={smtpUsername}
                onChange={(e) => setSmtpUsername(e.target.value)}
              />
            </Field>
            <Field label="Password" htmlFor="smtp-password">
              <Input
                id="smtp-password"
                type="password"
                autoComplete="off"
                value={smtpPassword}
                onChange={(e) => setSmtpPassword(e.target.value)}
              />
            </Field>
          </div>
          <Field label="From address" htmlFor="smtp-from">
            <Input
              id="smtp-from"
              type="email"
              autoComplete="off"
              placeholder="modbot@example.com"
              value={smtpFrom}
              onChange={(e) => setSmtpFrom(e.target.value)}
            />
          </Field>
        </div>

        <ErrorText>{error}</ErrorText>
      </WizardBody>
    </form>
  )
}
