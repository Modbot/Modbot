import { useEffect, useState } from 'react'
import { RuleBuilder } from '@/components/giveaways/RuleBuilder'
import { Button } from '@/components/ui/button'
import { api, type AutoInvites } from '@/lib/api'
import type { GiveawayBuilder, GiveawayRule } from '@/lib/giveaways'
import { EmptyRow } from '@/components/PanelGrid'
import { NumberField, Outcome, Row, Switch } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'

/**
 * Settings → Auto-invites (auto-invites design §10).
 *
 * The rule builder is the giveaway one, unchanged: the rules are the same tree, so the control is
 * the same control. The minutes and the "invite again after" are their own boxes rather than rules,
 * because a rule tree with "none of" in it can invert anything it can express — including the
 * five-minute floor (design §4.1).
 */
export function AutoInvitesSection() {
  const [loaded, setLoaded] = useState<AutoInvites | null>(null)
  const [enabled, setEnabled] = useState(false)
  const [minutes, setMinutes] = useState('')
  const [againAfter, setAgainAfter] = useState('')
  const [rules, setRules] = useState<GiveawayRule>({ kind: 'allOf', rules: [] })
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState('')
  const [error, setError] = useState('')

  const load = (next: AutoInvites) => {
    setLoaded(next)
    setEnabled(next.enabled)
    setMinutes(String(next.minutesInInstance))
    setAgainAfter(String(next.inviteAgainAfterDays))
    setRules(next.rules)
  }

  useEffect(() => {
    api
      .autoInvites()
      .then(load)
      .catch(() => setError('Could not load auto-invites.'))
  }, [])

  const builder: GiveawayBuilder | null = loaded
    ? {
        ruleKinds: loaded.ruleKinds,
        weightings: [],
        trustRanks: loaded.trustRanks,
        groupRoles: loaded.groupRoles,
        discordRoles: loaded.discordRoles,
        moderationFactRetentionDays: loaded.moderationFactRetentionDays,
        presenceFactRetentionDays: loaded.presenceFactRetentionDays,
      }
    : null

  const save = () => {
    setBusy(true)
    setSaved('')
    setError('')

    api
      .setAutoInvites({
        enabled,
        minutesInInstance: Number(minutes),
        inviteAgainAfterDays: Number(againAfter),
        rules,
      })
      .then((next) => {
        load(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsSection id="auto-invites" title="Auto-invites">
      <SettingsCard
        title="Auto-invites"
        span={12}
        footer={
          <>
            <Button size="xs" disabled={busy || !loaded} onClick={save}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
            <Outcome tone="ok">{saved}</Outcome>
            <Outcome tone="problem">{error}</Outcome>
          </>
        }
      >
        {!loaded && !error ? (
          <EmptyRow className="px-0">Loading…</EmptyRow>
        ) : (
          <>
            <Switch checked={enabled} onChange={setEnabled}>
              Invite people automatically
            </Switch>

            <div className="grid gap-3 sm:grid-cols-2">
              <NumberField
                label="Minutes in the instance"
                value={minutes}
                min={loaded?.minimumMinutesInInstance ?? 5}
                max={1440}
                onChange={setMinutes}
              />

              <NumberField
                label="Invite again after (days)"
                value={againAfter}
                min={1}
                max={3650}
                onChange={setAgainAfter}
              />
            </div>

            <div className="flex flex-col gap-1">
              <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Rules
              </span>
              <RuleBuilder rule={rules} builder={builder} onChange={setRules} />
            </div>

            <Row label="Invites sent" value={String(loaded?.invitesSent ?? 0)} mono />
            <Row
              label="Last invite"
              value={loaded?.lastInviteAt ? new Date(loaded.lastInviteAt).toLocaleString() : '—'}
              mono={!!loaded?.lastInviteAt}
            />
          </>
        )}
      </SettingsCard>
    </SettingsSection>
  )
}
