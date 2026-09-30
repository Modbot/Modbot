import { Fragment, useEffect, useState } from 'react'
import { Globe, Hash, Mail, type LucideIcon } from 'lucide-react'
import { Empty } from '@/components/ListParts'
import { PanelGrid } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { api, ApiError, type CurrentUser, type OnboardingStatus } from '@/lib/api'
import { integrations, settingsPath, type Integration, type IntegrationId, type IntegrationReading } from '@/lib/integrations'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import type { Tone } from '@/lib/status'
import { useGateHealth } from '@/lib/useGateHealth'
import { useSyncHealth } from '@/lib/useStatusRows'

/** The same pictures the phone's Menu gives the VRChat and Discord pages. */
const ICONS: Record<IntegrationId, LucideIcon> = {
  vrchat: Globe,
  discord: Hash,
  email: Mail,
}

const BADGE: Record<Tone, 'ok' | 'warn' | 'destructive' | 'outline'> = {
  ok: 'ok',
  warn: 'warn',
  bad: 'destructive',
  muted: 'outline',
}

/**
 * Integrations: a card for each outside service, its status and a Set up button into the Settings
 * topic where it is set up (lib/integrations.ts). Opened from the sidebar's Integrations heading.
 *
 * The saved settings come from the onboarding status, read fresh here rather than taken from the
 * shell's copy, so coming back from Settings shows what was just saved. The live status comes from
 * the reads the sidebar's status rows already make, shared rather than made again.
 */
export function Integrations({ me }: { me: CurrentUser }) {
  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api
      .onboardingStatus()
      .then((next) => !cancelled && setStatus(next))
      .catch((e: unknown) => !cancelled && setError(e instanceof ApiError ? e.message : 'Could not load the integrations.'))
    return () => {
      cancelled = true
    }
  }, [])

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!status) return <Empty>Loading…</Empty>

  // The Discord bot's own state is part of the Health read, which needs See Modbot's log. Without
  // it the read is not made at all, and a saved bot says "Unknown" rather than a guess.
  return can(me, 'ViewOperationalLog') ? <WithBotState status={status} /> : <Cards status={status} />
}

function WithBotState({ status }: { status: OnboardingStatus }) {
  const health = useSyncHealth()
  return <Cards status={status} discordBot={health ? health.discordBot : undefined} />
}

function Cards({
  status,
  discordBot,
}: {
  status: OnboardingStatus
  discordBot?: IntegrationReading['discordBot']
}) {
  const { gate, failed } = useGateHealth()

  const list = integrations({
    gate: failed ? null : (gate?.status ?? null),
    discordConfigured: status.integrations.discordConfigured,
    discordBot,
    smtpConfigured: status.integrations.smtpConfigured,
  })

  return (
    <PanelGrid className="grid-cols-1 md:grid-cols-2 xl:grid-cols-3">
      {list.map((item) => (
        <IntegrationCard key={item.id} item={item} />
      ))}
    </PanelGrid>
  )
}

function IntegrationCard({ item }: { item: Integration }) {
  const Icon = ICONS[item.id]
  const setUp = settingsPath(item.topic)

  return (
    <Card>
      <CardHeader>
        <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden />
        <CardTitle>{item.name}</CardTitle>
        <CardAction>
          <Badge variant={BADGE[item.state.tone]}>{item.state.label}</Badge>
        </CardAction>
      </CardHeader>
      <CardContent className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {item.parts.map((part, i) => (
          <Fragment key={part.name}>
            {i > 0 && ' · '}
            {part.topic ? (
              <a
                href={settingsPath(part.topic)}
                onClick={followLink(settingsPath(part.topic))}
                className="text-link underline-offset-4 hover:underline"
              >
                {part.name}
              </a>
            ) : (
              part.name
            )}
          </Fragment>
        ))}
      </CardContent>
      <CardFooter>
        <Button size="sm" asChild>
          <a href={setUp} onClick={followLink(setUp)}>
            Set up
          </a>
        </Button>
      </CardFooter>
    </Card>
  )
}
