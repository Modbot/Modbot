import { ArrowDown } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button'
import { Feature, SiteFooter, SiteHeader } from '@/components/Site'
import { SourceBadge } from '@/components/SourceBadge'
import { OPEN_MY_SERVER } from '@/lib/links'
import { cn } from '@/lib/utils'
import { AppMock } from '@/mock/AppMock'
import { AnalyticsPanel } from '@/visuals/AnalyticsPanel'
import { CaseFileMock } from '@/visuals/CaseFileMock'
import { ChatMock } from '@/visuals/ChatMock'
import { DiscordCards } from '@/visuals/DiscordCards'
import { OverlayMock } from '@/visuals/OverlayMock'

/*
 * Every claim on this page is something Modbot does today; the planned work is only under "Not built
 * yet". When a feature changes, change its words here in the same commit.
 */

/** @param privacy Whether the privacy policy was built, for the footer link. */
export default function App({ privacy = false }: { privacy?: boolean }) {
  return (
    <>
      <a
        href="#main"
        className="sr-only z-50 rounded-md bg-primary px-3 py-2 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2"
      >
        Skip to content
      </a>
      <SiteHeader />
      <main id="main" tabIndex={-1} className="outline-none">
        <Hero />
        <LiveFacts />
        <Features />
        <Everyday />
        <SelfHost />
        <Coming />
        <Closing />
      </main>
      <SiteFooter privacy={privacy} />
    </>
  )
}

function Hero() {
  return (
    <section id="top" aria-labelledby="hero-title" className="relative isolate">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-x-0 top-[24rem] -z-10 h-[44rem] opacity-60 dark:opacity-40"
        style={{ background: 'radial-gradient(50% 50% at 50% 50%, color-mix(in oklab, var(--primary) 28%, transparent), transparent 70%)' }}
      />
      <div className="mx-auto max-w-6xl px-4 pt-12 sm:px-6 sm:pt-16 md:pt-20">
        <div>
          <p className="text-[0.9375rem] text-muted-foreground">
            Open-source moderation for VRChat groups
          </p>
          <h1 id="hero-title" className="display mt-4 text-[3.5rem] leading-[0.9] sm:text-[5.5rem] lg:text-[8.25rem]">
            Moderation <br className="hidden sm:block" />
            that remembers.
          </h1>
          <div className="mt-8 grid gap-8 md:grid-cols-12 md:items-end">
            <p className="max-w-[36rem] text-lg text-pretty text-muted-foreground sm:text-xl md:col-span-7">
              Modbot watches your group&rsquo;s instances, keeps the history VRChat throws away, and gives your team
              the tools it doesn&rsquo;t. It runs on your own server.
            </p>
            <div className="flex flex-wrap gap-3 md:col-span-5 md:justify-end">
              <a href="#self-host" className={buttonVariants({ size: 'lg' })}>
                Host your own
              </a>
              <a href={OPEN_MY_SERVER} className={buttonVariants({ variant: 'outline', size: 'lg' })}>
                Open my server
              </a>
            </div>
          </div>
        </div>

        <div className="enter-late mt-14 sm:mt-16">
          <AppMock label="Sample of Modbot's Live page. Names, worlds and instance numbers open popups." className="h-[40rem] sm:h-[38rem]" />
          <p className="mt-3 flex items-center justify-center gap-2 text-sm text-muted-foreground">
            <ArrowDown className="size-3.5 rotate-180" aria-hidden="true" />
            Sample data. Click a name, a world or an instance number.
          </p>
        </div>
      </div>
    </section>
  )
}

function LiveFacts() {
  const facts: [string, string][] = [
    ['Every open instance', 'Each group instance appears as it opens, with VRChat’s own head count.'],
    ['Who is inside', 'While a moderator’s desktop client is in the room, you see each person and when they arrived.'],
    ['Past trouble, marked', 'People with earlier moderation actions stand out in the list.'],
    ['Nothing wasted', 'The page refreshes every five seconds, and stops while its tab is hidden.'],
  ]

  return (
    <section id="live" aria-labelledby="live-title" className="mx-auto max-w-6xl px-4 pt-20 sm:px-6 md:pt-28">
      <div className="flex flex-wrap items-center gap-1.5" style={{ ['--text-small' as string]: '0.8125rem' }}>
        <SourceBadge source="VRChat" className="bg-card" />
        <SourceBadge source="Client" className="bg-card" />
      </div>
      <div className="mt-5 grid gap-5 lg:grid-cols-12 lg:items-end">
        <h2 id="live-title" className="display max-w-[18ch] text-[2.5rem] leading-[1.02] sm:text-[3.25rem] lg:col-span-6">
          Every instance, as it happens.
        </h2>
        <p className="max-w-[34rem] text-lg text-pretty text-muted-foreground lg:col-span-6">
          The Live page is where a moderator starts the evening: every room the group has open, how full it is,
          and who just walked in.
        </p>
      </div>
      <dl className="mt-10 grid gap-x-8 gap-y-6 border-t pt-8 sm:grid-cols-2 lg:grid-cols-4">
        {facts.map(([term, detail]) => (
          <div key={term}>
            <dt className="font-semibold">{term}</dt>
            <dd className="mt-1 text-[0.96875rem] text-pretty text-muted-foreground">{detail}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}

function Features() {
  return (
    <>
      <Feature
        id="popups"
        sources={['VRChat', 'Sync', 'Client', 'Modbot']}
        title="Click any name. Keep your place."
        lead="A person, a world or an instance opens over the page you’re on. Open another from inside it and they stack; Back takes you down one."
        facts={[
          <><strong className="font-semibold">A person:</strong> their VRChat profile, membership and roles, everything recorded about them, case files and time in world.</>,
          <><strong className="font-semibold">A world:</strong> every group instance it has hosted, and visitors per day.</>,
          <><strong className="font-semibold">An instance:</strong> who was seen in it, and what happened there.</>,
          'The stack lives in the address, so a link opens the same popups.',
        ]}
        visual={
          <AppMock
            label="Sample of Modbot's popups, opened on a person."
            startWith={[{ kind: 'world', id: 'wrld_harbor' }, { kind: 'person', id: 'usr_teaspoon' }]}
            play={false}
            className="h-[34rem]"
          />
        }
      />

      <Feature
        id="discord"
        flip
        sources={['Discord']}
        title="Your Discord knows when the doors open."
        lead="The bot posts a card for each group instance, keeps it current as people come and go, and adds a Join button that opens VRChat."
        facts={[
          'Send bans, kicks, joins, role changes, case files and more to any channels you like, each narrowed to certain people or roles.',
          <><code className="font-mono text-[0.9em]">/lookup</code> a person or see <code className="font-mono text-[0.9em]">/recent</code> actions, with replies only you can see.</>,
          'Names appear on a card only while a moderator is watching, and you can turn them off.',
          'Commands follow the same permissions as the web app.',
        ]}
        visual={<DiscordCards />}
      />

      <Feature
        id="case-files"
        sources={['Modbot']}
        title="Every ban gets a case file."
        lead="Pick the reasons, write it up, attach screenshots and clips. Modbot keeps a copy of the person’s profile as it was at that moment."
        facts={[
          'PNG, JPEG, WebP, GIF, MP4 and WebM, checked by what is inside the file, not its name.',
          'Stored in an S3-compatible bucket, a mounted folder or PostgreSQL. Every file is fingerprinted.',
          'Full edit history. A case file can be withdrawn with a note, never deleted.',
          'The Bans page counts recent bans that still have no case file.',
        ]}
        visual={<CaseFileMock />}
      />

      <Feature
        id="client"
        flip
        sources={['Client']}
        title="A moderator’s eyes, inside the instance."
        lead="The Windows client reads VRChat’s own log as you play and reports who comes and goes. In SteamVR, an overlay shows who is in the room and warns you when someone with a record walks in."
        facts={[
          'Windows 10 and 11, installed without administrator rights.',
          'Pairs with your server through my.modbot.co, using a one-time link that lasts five minutes.',
          'Its token can only report presence and read the room list, and is stored encrypted to your Windows account.',
          'Pair it with more than one group.',
        ]}
        visual={<OverlayMock />}
      />

      <Feature
        id="ai"
        sources={['VRChat', 'Modbot']}
        title="AI help that answers to you."
        lead="Connect the AI provider you choose, and Modbot can answer your team’s questions from its own records, check profiles against your rules, and sum up the week."
        facts={[
          <><strong className="font-semibold">Chat:</strong> ask about a person, a world or the group. It looks things up with the asker’s own permissions and never takes an action.</>,
          <><strong className="font-semibold">Flags:</strong> VRChat names and bios checked against term lists you write or subscribe to, and against AI topics. A moderator reviews every flag.</>,
          <><strong className="font-semibold">Insights:</strong> scheduled summaries of your group’s own figures.</>,
          'A daily call limit for moderation, and every call’s usage recorded by feature. Nothing goes to a provider until you set one up.',
        ]}
        visual={<ChatMock />}
      />

      <Feature
        id="analytics"
        flip
        sources={['VRChat', 'Client', 'Modbot']}
        title="Numbers your team can act on."
        lead="Four pages, each answering one question about your group."
        facts={[
          <><strong className="font-semibold">My Group:</strong> members, joins and leaves, invites and join requests, how long people stay.</>,
          <><strong className="font-semibold">My Team:</strong> actions per moderator, and the gaps when the last moderator left and people stayed.</>,
          <><strong className="font-semibold">Worlds:</strong> where your people actually spend their time.</>,
          <><strong className="font-semibold">Instances:</strong> how long they run, and the busiest hours in your time zone.</>,
        ]}
        visual={<AnalyticsPanel />}
      />
    </>
  )
}

function Everyday() {
  const items: [string, string][] = [
    ['Audit log', 'One timeline from VRChat, the syncs, the client and Modbot itself. Filter by type, source, person and date.'],
    ['Members and bans', 'Search the member list by name or id, filter by role, and see who left and when.'],
    ['Roles', 'Administrator, Moderator and Viewer, or your own roles built from 21 permissions.'],
    ['Staff accounts', 'Every moderator signs in as themselves, after proving their VRChat account with a code in their bio.'],
    ['Reviews', 'Repeat offenders counted over 30 days, and a review when a moderator’s actions look unusual.'],
    ['Your own tools', 'API keys that carry a person’s permissions, and every new event over a live WebSocket.'],
    ['Sync health', 'Each background sync and VRChat’s rate limits, in one place. Modbot stops at VRChat’s first “slow down” instead of pushing on.'],
    ['VR mode', 'Dense, comfortable or VR: bigger targets and text for using the web app from a headset.'],
  ]

  return (
    <section id="everyday" aria-labelledby="everyday-title" className="border-y bg-card">
      <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
        <h2 id="everyday-title" className="display max-w-[16ch] text-[2.5rem] leading-[1.02] sm:text-[3.25rem]">
          And the everyday parts.
        </h2>
        <dl className="mt-12 grid gap-px overflow-hidden rounded-xl border bg-border sm:grid-cols-2 lg:grid-cols-4">
          {items.map(([term, detail]) => (
            <div key={term} className="bg-card p-5">
              <dt className="font-semibold">{term}</dt>
              <dd className="mt-1.5 text-[0.9375rem] text-pretty text-muted-foreground">{detail}</dd>
            </div>
          ))}
        </dl>
      </div>
    </section>
  )
}

function SelfHost() {
  const steps = [
    'Create your administrator account',
    'Connect a VRChat account',
    'Check the connection',
    'Link your VRChat account',
    'Choose the group to manage',
    'Public address, Discord and email',
  ]

  return (
    <section id="self-host" aria-labelledby="self-host-title" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
      <div className="grid gap-12 lg:grid-cols-12">
        <div className="min-w-0 lg:col-span-6">
          <h2 id="self-host-title" className="display text-[2.75rem] leading-[1] sm:text-[4rem]">
            Your server. Your data.
          </h2>
          <p className="mt-5 max-w-[34rem] text-lg text-pretty text-muted-foreground">
            Modbot is one container and one PostgreSQL database. Your group&rsquo;s records live there, and nowhere else.
          </p>
          <ul className="mt-8 max-w-[34rem] border-t">
            {[
              'No Redis, no search server, no hosted sign-in, no third-party account.',
              'Works with no contact to modbot.co at all. my.modbot.co is a shortcut, never a requirement.',
              'Usage reports have no field for your group or its members, and you can turn them off.',
              'Open source under AGPL-3.0: run it, read it, change it.',
            ].map((fact) => (
              <li key={fact} className="border-b py-2.5 text-[0.96875rem] text-pretty">
                {fact}
              </li>
            ))}
          </ul>
        </div>

        <div className="flex min-w-0 flex-col gap-4 lg:col-span-6">
          <div className="overflow-hidden rounded-xl border bg-[#0d0e12] text-[#e6e8ee] shadow-[0_30px_80px_-40px_rgb(22_24_31/0.5)]">
            <div className="border-b border-[#272b36] px-4 py-2.5 text-sm text-[#9aa1b1]">One setting. That&rsquo;s all.</div>
            <pre className="overflow-x-auto px-4 py-4 font-mono text-[0.875rem] leading-7">
              <code>
                <span className="text-[#b6acff]">DATABASE_URL</span>=postgres://user:password@host:5432/modbot{'\n'}
                <span className="text-[#9aa1b1]"># PORT is optional and defaults to 8080</span>
              </code>
            </pre>
          </div>
          <div className="rounded-xl border bg-card p-5">
            <h3 className="font-semibold">Everything else is set up in your browser</h3>
            <ol className="mt-3 grid">
              {steps.map((step, i) => (
                <li key={step} className="flex gap-3 border-t py-2 text-[0.9375rem]">
                  <span className="w-4 shrink-0 font-mono text-muted-foreground tabular-nums">{i + 1}</span>
                  {step}
                </li>
              ))}
            </ol>
          </div>
          <p className="text-[0.9375rem] text-pretty text-muted-foreground">
            Runs anywhere Docker does. On Railway it reads the platform&rsquo;s own settings, including a storage bucket for evidence.
          </p>
        </div>
      </div>
    </section>
  )
}

function Coming() {
  const items = [
    ['Actions from Modbot', 'Kick, ban and warn without switching to VRChat.'],
    ['Discord sync', 'Linked accounts, roles and bans kept in step with the group, and your rules applied to Discord chat.'],
    ['Segments and giveaways', 'Find “regulars with 10+ hours this month and no bans”, then draw fairly.'],
    ['Shared warnings', 'Flags from the groups someone belongs to, and warnings shared between groups.'],
  ]

  return (
    <section id="coming" aria-labelledby="coming-title" className="mx-auto max-w-6xl px-4 pb-20 sm:px-6 md:pb-28">
      <div className="rounded-2xl border border-dashed p-6 sm:p-10">
        <h2 id="coming-title" className="display text-[2rem] leading-[1.05] sm:text-[2.5rem]">
          Not built yet
        </h2>
        <p className="mt-2 max-w-[36rem] text-muted-foreground">Designed and planned, but not in Modbot today.</p>
        <dl className="mt-8 grid gap-x-8 gap-y-5 sm:grid-cols-2">
          {items.map(([term, detail]) => (
            <div key={term}>
              <dt className="font-semibold">{term}</dt>
              <dd className="mt-1 text-[0.9375rem] text-pretty text-muted-foreground">{detail}</dd>
            </div>
          ))}
        </dl>
      </div>
    </section>
  )
}

function Closing() {
  return (
    <section aria-labelledby="closing-title" className="bg-[#5b4bd6] text-white">
      <div className="mx-auto grid max-w-6xl gap-8 px-4 py-16 sm:px-6 md:grid-cols-12 md:items-end md:py-20">
        <div className="md:col-span-8">
          <h2 id="closing-title" className="display text-[2.5rem] leading-[1] sm:text-[3.5rem]">
            Already running Modbot?
          </h2>
          <p className="mt-3 max-w-[34rem] text-lg text-pretty">
            Pick your server on my.modbot.co and go straight to it.
          </p>
        </div>
        <div className="flex flex-wrap gap-3 md:col-span-4 md:justify-end">
          <a
            href={OPEN_MY_SERVER}
            className={cn(buttonVariants({ size: 'lg' }), 'bg-white text-[#3f31a8] hover:bg-white/90')}
          >
            Open my server
          </a>
        </div>
      </div>
    </section>
  )
}
