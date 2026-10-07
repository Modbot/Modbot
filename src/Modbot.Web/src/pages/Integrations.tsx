import { Fragment, useEffect, useState } from 'react'
import { CalendarDays, Cloud, Globe, Hash, Mail, Radio, type LucideIcon } from 'lucide-react'
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

/** The same pictures the phone's Menu gives the VRChat and Discord pages. */
const ICONS: Record<IntegrationId, LucideIcon> = {
  vrchat: Globe,
  discord: Hash,
  email: Mail,
  google: CalendarDays,
  bluesky: Cloud,
  twitch: Radio,
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
 * shell's copy, so coming back from Settings shows what was just saved. VRChat's live status is the
 * gate read the sidebar already makes, shared rather than made again; Discord's is the bot state
 * read, because the sidebar's Health read needs See Modbot's log and this page only Change settings.
 * Google Calendar's, Bluesky's and Twitch's are their own settings reads, which need Change settings too.
 */
export function Integrations({ me }: { me: CurrentUser }) {
  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Bumped by Try again.
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false
    api
      .onboardingStatus()
      .then((next) => !cancelled && setStatus(next))
      .catch((e: unknown) => !cancelled && setError(e instanceof ApiError ? e.message : 'Could not load the integrations.'))
    return () => {
      cancelled = true
    }
  }, [tries])

  if (error) {
    return (
      <Empty
        tone="danger"
        onTryAgain={() => {
          setError(null)
          setTries((n) => n + 1)
        }}
      >
        {error}
      </Empty>
    )
  }
  if (!status) return <Empty tone="loading" />

  // The Discord bot's state comes from its own small read, which needs Change settings like the page
  // (the full bot report in the Health read needs See Modbot's log). Without it the read is not made
  // at all, and a saved bot says "Unknown" rather than a guess. The same for Google Calendar.
  return can(me, 'ManageSettings') ? <WithBotState status={status} /> : <Cards status={status} />
}

/** How often the bot's state is read again while the page is open: the sidebar's status rows' pace. */
const BOT_STATE_EVERY_MS = 30_000

function WithBotState({ status }: { status: OnboardingStatus }) {
  const [bot, setBot] = useState<IntegrationReading['discordBot']>(undefined)
  const [google, setGoogle] = useState<IntegrationReading['googleCalendar']>(undefined)
  const [bluesky, setBluesky] = useState<IntegrationReading['bluesky']>(undefined)
  const [twitch, setTwitch] = useState<IntegrationReading['twitch']>(undefined)

  // Read once: each changes only when somebody saves or checks it in Settings.
  useEffect(() => {
    let cancelled = false
    api
      .googleCalendarSettings()
      .then((view) => !cancelled && setGoogle(view))
      .catch(() => !cancelled && setGoogle(undefined))
    api
      .blueskySettings()
      .then((view) => !cancelled && setBluesky(view))
      .catch(() => !cancelled && setBluesky(undefined))
    api
      .twitchSettings()
      .then((view) => !cancelled && setTwitch(view))
      .catch(() => !cancelled && setTwitch(undefined))
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    const read = () =>
      api
        .discordBotState()
        .then((view) => !cancelled && setBot(view.state))
        .catch(() => !cancelled && setBot(undefined))

    void read()
    const timer = window.setInterval(() => void read(), BOT_STATE_EVERY_MS)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [])

  return <Cards status={status} discordBot={bot} googleCalendar={google} bluesky={bluesky} twitch={twitch} />
}

function Cards({
  status,
  discordBot,
  googleCalendar,
  bluesky,
  twitch,
}: {
  status: OnboardingStatus
  discordBot?: IntegrationReading['discordBot']
  googleCalendar?: IntegrationReading['googleCalendar']
  bluesky?: IntegrationReading['bluesky']
  twitch?: IntegrationReading['twitch']
}) {
  const { gate, failed } = useGateHealth()

  const list = integrations({
    gate: failed ? null : (gate?.status ?? null),
    discordConfigured: status.integrations.discordConfigured,
    discordBot,
    smtpConfigured: status.integrations.smtpConfigured,
    googleCalendar,
    bluesky,
    twitch,
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
