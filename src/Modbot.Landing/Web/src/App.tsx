import { buttonVariants } from '@/components/ui/button'
import { Feature, SiteFooter, SiteHeader } from '@/components/Site'
import { SourceBadge } from '@/components/SourceBadge'
import { OPEN_MY_SERVER, SELF_HOSTING_GUIDE } from '@/lib/links'
import { cn } from '@/lib/utils'
import { AppMock } from '@/mock/AppMock'
import { AnalyticsPanel } from '@/visuals/AnalyticsPanel'
import { CaseFileMock } from '@/visuals/CaseFileMock'
import { ChatMock } from '@/visuals/ChatMock'
import { DiscordCards } from '@/visuals/DiscordCards'
import { OverlayMock } from '@/visuals/OverlayMock'

/*
 * Every claim on this page is something Modbot does today; the planned work is only under "Not built
 * yet". When a feature changes, change its words here in the same commit. The words follow the brand
 * design (.agent/specs/2026-09-16-brand-design.md §6): plain, specific, no superlatives.
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
        <Compare />
        <LiveFacts />
        <Features />
        <Everyday />
        <SelfHost />
        <Rules />
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
      <div className="mx-auto max-w-6xl px-4 pt-10 sm:px-6 sm:pt-14 md:pt-20">
        <div className="grid items-center gap-8 lg:grid-cols-12 lg:gap-12">
          <div className="min-w-0 lg:col-span-8">
            <p className="text-[0.9375rem] text-muted-foreground">Open source, under AGPL-3.0</p>
            <h1 id="hero-title" className="display mt-3.5 text-[2.75rem] leading-[0.98] sm:text-[4rem] lg:text-[5rem]">
              Self-hosted moderation for VRChat groups.
            </h1>
            <p className="mt-6 max-w-[36rem] text-lg text-pretty text-muted-foreground sm:text-xl">
              Modbot records why each person was banned and who was in the instance at the time. It keeps that
              record for as long as you want, in one container and one PostgreSQL database on a server you control.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              <a href={SELF_HOSTING_GUIDE} className={buttonVariants({ size: 'lg' })}>
                Host your own
              </a>
              <a href="#compare" className={buttonVariants({ variant: 'outline', size: 'lg' })}>
                Compare with VRChat
              </a>
            </div>
            <ul
              aria-label="At a glance"
              className="mt-6 flex flex-col gap-0.5 text-[0.9375rem] text-muted-foreground sm:flex-row sm:flex-wrap sm:gap-x-3 [&>li+li]:before:hidden sm:[&>li+li]:before:inline sm:[&>li+li]:before:pr-3 sm:[&>li+li]:before:content-['·']"
            >
              <li>One setting to start</li>
              <li>Web app, Discord bot, Windows client</li>
              <li>Not affiliated with VRChat Inc.</li>
            </ul>
          </div>
          {/* The full mascot, at a quarter of the hero and no more: the product is the hero. */}
          <div aria-hidden="true" className="order-first lg:order-none lg:col-span-4">
            <div className="grid place-items-center lg:aspect-square lg:max-w-[20rem] lg:rounded-xl lg:bg-accent/60">
              <img
                src="/mascot.png"
                alt=""
                width={445}
                height={512}
                className="w-28 drop-shadow-[0_18px_30px_rgb(40_30_90/0.18)] lg:w-[84%]"
              />
            </div>
          </div>
        </div>

        <div className="enter-late mt-12 sm:mt-16">
          <AppMock
            label="Sample of Modbot's Live page. Names, worlds and instance numbers open popups."
            className="h-[40rem] sm:h-[38rem]"
          />
          <p className="mt-3 text-center text-sm text-muted-foreground">
            Sample data. Names and worlds are made up. Click a name, a world or an instance number.
          </p>
        </div>
      </div>
    </section>
  )
}

/**
 * The problem statement, in the moderators' own terms. VRChat's side is what its group pages show
 * today; the audit-log figure is the project's own measurement (.agent/research, oldest entry 31 days).
 */
function Compare() {
  const rows: [string, string, string][] = [
    [
      'Ban list',
      'A list of profiles. No search, and no reason on record.',
      'Search by name or id, and a case file for each ban with the reasons, a write-up and the evidence.',
    ],
    [
      'Audit log',
      'About a month of events, with no filter by what happened.',
      'Every event, parsed with its data, kept for as long as you choose. Filter by type, source, person and date.',
    ],
    [
      'Who did what',
      'The moderator who took an action is not kept with it.',
      'Each action carries the moderator who took it, so the owner can read what the team did.',
    ],
    [
      'Who was in the room',
      'You see the people in an instance while you are inside it.',
      "While a moderator's desktop client is in the room, it reports who arrives and leaves, and Modbot keeps that.",
    ],
  ]

  return (
    <section id="compare" aria-labelledby="compare-title" className="mx-auto max-w-6xl px-4 pt-20 sm:px-6 md:pt-28">
      <div className="grid gap-5 lg:grid-cols-12 lg:items-end">
        <h2 id="compare-title" className="display max-w-[18ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem] lg:col-span-6">
          Compared with VRChat&rsquo;s group tools.
        </h2>
        <p className="max-w-[34rem] text-lg text-pretty text-muted-foreground lg:col-span-6">
          Four records a moderator team asks for, and how far back each tool can answer.
        </p>
      </div>
      <div role="table" aria-label="VRChat's group tools compared with Modbot" className="mt-10 overflow-hidden rounded-xl border bg-card">
        <div role="row" className="hidden grid-cols-[11rem_1fr_1fr] gap-x-6 bg-muted px-5 py-2.5 text-[0.8125rem] font-semibold tracking-[0.04em] text-muted-foreground uppercase md:grid">
          <span role="columnheader">Record</span>
          <span role="columnheader">VRChat</span>
          <span role="columnheader">Modbot</span>
        </div>
        {rows.map(([record, vrchat, modbot]) => (
          <div key={record} role="row" className="grid gap-2 border-t px-5 py-4 md:grid-cols-[11rem_1fr_1fr] md:gap-x-6">
            <span role="rowheader" className="font-semibold">
              {record}
            </span>
            <p role="cell" className="text-[0.96875rem] text-pretty">
              <span className="block text-[0.75rem] font-semibold tracking-[0.04em] text-muted-foreground uppercase md:hidden">VRChat</span>
              {vrchat}
            </p>
            <p role="cell" className="text-[0.96875rem] text-pretty">
              <span className="block text-[0.75rem] font-semibold tracking-[0.04em] text-muted-foreground uppercase md:hidden">Modbot</span>
              {modbot}
            </p>
          </div>
        ))}
      </div>
    </section>
  )
}

function LiveFacts() {
  const facts: [string, string][] = [
    ['Every open instance', 'Each group instance appears as it opens, with VRChat’s own head count.'],
    ['Who is inside', 'While a moderator’s desktop client is in the room, you see each person and when they arrived.'],
    ['Earlier actions stand out', 'People with earlier moderation actions stand out in the list.'],
    ['Refreshes on its own', 'The page refreshes every five seconds, and stops while its tab is hidden.'],
  ]

  return (
    <section id="live" aria-labelledby="live-title" className="mx-auto max-w-6xl px-4 pt-20 sm:px-6 md:pt-28">
      <div className="flex flex-wrap items-center gap-1.5" style={{ ['--text-small' as string]: '0.8125rem' }}>
        <SourceBadge source="VRChat" className="bg-card" />
        <SourceBadge source="Client" className="bg-card" />
      </div>
      <div className="mt-5 grid gap-5 lg:grid-cols-12 lg:items-end">
        <h2 id="live-title" className="display max-w-[18ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem] lg:col-span-6">
          Every instance, as it happens.
        </h2>
        <p className="max-w-[34rem] text-lg text-pretty text-muted-foreground lg:col-span-6">
          The Live page shows every room the group has open, how full it is, and who arrived last.
        </p>
      </div>
      <Tiles items={facts} className="mt-10" />
    </section>
  )
}

function Tiles({ items, className }: { items: [string, string][]; className?: string }) {
  return (
    <dl className={cn('grid gap-px overflow-hidden rounded-xl border bg-border sm:grid-cols-2 lg:grid-cols-4', className)}>
      {items.map(([term, detail]) => (
        <div key={term} className="bg-card p-5">
          <dt className="font-semibold">{term}</dt>
          <dd className="mt-1.5 text-[0.9375rem] text-pretty text-muted-foreground">{detail}</dd>
        </div>
      ))}
    </dl>
  )
}

function Features() {
  return (
    <>
      <Feature
        id="popups"
        sources={['VRChat', 'Sync', 'Client', 'Modbot']}
        title="Open a name without losing the page."
        lead="A person, a world or an instance opens over the page you are on. Open another from inside it and they stack. Back takes you down one."
        facts={[
          <><strong className="font-semibold">A person:</strong> their VRChat profile, membership and roles, everything recorded about them, case files and time in world.</>,
          <><strong className="font-semibold">A world:</strong> every group instance it has hosted, and visitors per day.</>,
          <><strong className="font-semibold">An instance:</strong> who was seen in it, and what happened there.</>,
          'The stack is part of the address, so a link opens the same popups.',
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
        title="Instance cards in your Discord."
        lead="The bot posts a card for each group instance and updates it as people come and go. A Join button on the card opens VRChat."
        facts={[
          'Send bans, kicks, joins, role changes and case files to the channels you choose. Each channel can be limited to certain people or roles.',
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
          'PNG, JPEG, WebP, GIF, MP4 and WebM, checked by their contents, so a renamed file does not pass.',
          'Stored in an S3-compatible bucket, a mounted folder or PostgreSQL. Every file is fingerprinted.',
          'Every edit is kept. A case file can be withdrawn with a note, and it stays on record.',
          'The Bans page counts recent bans that still have no case file.',
        ]}
        visual={<CaseFileMock />}
      />

      <Feature
        id="client"
        flip
        sources={['Client']}
        title="Who is in the room, from a moderator’s own PC."
        lead="The Windows client reads VRChat’s own log as you play and reports who comes and goes. In SteamVR, an overlay lists the room and warns you when someone with a record walks in."
        facts={[
          'Windows 10 and 11, installed without administrator rights. Quest and phone players are covered by the web app and the Discord bot.',
          'Pairs with your server through my.modbot.co, using a one-time link that lasts five minutes.',
          'Its token can only report presence and read the room list, and is stored encrypted to your Windows account.',
          'Pair it with more than one group.',
        ]}
        visual={<OverlayMock />}
      />

      <Feature
        id="ai"
        sources={['VRChat', 'Modbot']}
        title="AI help, with a provider you choose."
        lead="Connect a provider of your choice. Nothing goes to it until you do."
        facts={[
          <><strong className="font-semibold">Chat:</strong> ask about a person, a world or the group. It looks things up with the asker’s own permissions and never takes an action.</>,
          <><strong className="font-semibold">Flags:</strong> VRChat names and bios checked against term lists you write or subscribe to, and against AI topics. A moderator reviews every flag.</>,
          <><strong className="font-semibold">Insights:</strong> scheduled summaries of your group’s own figures.</>,
          'A daily call limit for moderation, and every call’s usage recorded by feature.',
        ]}
        visual={<ChatMock />}
      />

      <Feature
        id="analytics"
        flip
        sources={['VRChat', 'Client', 'Modbot']}
        title="Four pages of numbers."
        lead="Each one answers a single question about your group."
        facts={[
          <><strong className="font-semibold">My Group:</strong> members, joins and leaves, invites and join requests, how long people stay.</>,
          <><strong className="font-semibold">My Team:</strong> actions per moderator, and the gaps when the last moderator left and people stayed.</>,
          <><strong className="font-semibold">Worlds:</strong> where your people spend their time.</>,
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
    ['Sync health', 'Each background sync and VRChat’s rate limits, in one place. Modbot stops at VRChat’s first “slow down” and waits.'],
    ['VR mode', 'Dense, comfortable or VR: bigger targets and text for using the web app from a headset.'],
  ]

  return (
    <section id="everyday" aria-labelledby="everyday-title" className="border-y bg-card">
      <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
        <h2 id="everyday-title" className="display max-w-[16ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem]">
          The everyday parts.
        </h2>
        <Tiles items={items} className="mt-12" />
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
          <h2 id="self-host-title" className="display text-[2.5rem] leading-[1] sm:text-[3.5rem]">
            Runs on your own server.
          </h2>
          <p className="mt-5 max-w-[34rem] text-lg text-pretty text-muted-foreground">
            Modbot is one container and one PostgreSQL database, and your group&rsquo;s records are stored there.
          </p>
          <ul className="mt-8 max-w-[34rem] border-t">
            {[
              'Every record Modbot keeps is in your database. Evidence files go to an S3-compatible bucket, a mounted folder or that same database.',
              'The server talks to VRChat. It also talks to Discord, to your mail server and to an AI provider when you set each one up. It never contacts modbot.co.',
              'Usage reports have no field for your group or its members, and you can turn them off.',
              'The desktop client also sends the events it reads from VRChat’s log, for every instance it is in, to Modbot Cloud as a backup. This is on by default, and the client has a switch for it.',
              'PostgreSQL is the only other service it needs.',
            ].map((fact) => (
              <li key={fact} className="border-b py-2.5 text-[0.96875rem] text-pretty">
                {fact}
              </li>
            ))}
          </ul>
        </div>

        <div className="flex min-w-0 flex-col gap-4 lg:col-span-6">
          <div className="window-shadow overflow-hidden rounded-xl border border-[#2a2738] bg-[#0f0e15] text-[#e6e6eb]">
            <div className="border-b border-[#2a2738] px-4 py-2.5 text-sm text-[#a5a3b8]">The one setting</div>
            <pre className="overflow-x-auto px-4 py-4 font-mono text-[0.875rem] leading-7">
              <code>
                <span className="text-[#b6acff]">DATABASE_URL</span>=postgres://user:password@host:5432/modbot{'\n'}
                <span className="text-[#a5a3b8]"># PORT is optional and defaults to 8080</span>
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
            Runs anywhere Docker does. On Railway it reads the platform&rsquo;s own settings, including a storage bucket for evidence.{' '}
            <a href={SELF_HOSTING_GUIDE} className="font-medium text-foreground underline underline-offset-4">
              Read the self-hosting guide
            </a>
          </p>
        </div>
      </div>
    </section>
  )
}

/** The facts a head moderator checks before handing a tool a VRChat login. Sources: docs/security.md, docs/discord-bot.md. */
function Rules() {
  const items: [string, string[]][] = [
    [
      'The VRChat client',
      [
        'Modbot does not modify the game. The server reads VRChat’s API the way the VRChat website does, and the desktop client reads the log file VRChat writes on your PC.',
      ],
    ],
    [
      'The VRChat account',
      [
        'Modbot signs in as one account you give it, and that account must be a moderator of the group. VRChat has no limited-access login for tools, so the account’s password and two-factor secret are stored encrypted in your database.',
        'It uses that one account, with one optional proxy for hosts VRChat’s firewall blocks. When VRChat answers “slow down”, it stops.',
      ],
    ],
    [
      'VRChat’s API',
      [
        'VRChat offers no support for third-party use of its API. Modbot is an independent, community-made tool. It is not affiliated with, endorsed by or supported by VRChat Inc.',
      ],
    ],
    [
      'The Discord bot’s permissions',
      ['View Channels, Send Messages, Embed Links, Read Message History and View Audit Log. It does not ask for Administrator.'],
    ],
  ]

  return (
    <section id="rules" aria-labelledby="rules-title" className="border-y bg-card">
      <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
        <div className="grid gap-5 lg:grid-cols-12 lg:items-end">
          <h2 id="rules-title" className="display max-w-[18ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem] lg:col-span-6">
            Modbot and VRChat&rsquo;s rules.
          </h2>
          <p className="max-w-[34rem] text-lg text-pretty text-muted-foreground lg:col-span-6">
            What Modbot does with the VRChat account it is given, and with your Discord server.
          </p>
        </div>
        <dl className="mt-10 grid gap-x-12 gap-y-8 md:grid-cols-2">
          {items.map(([term, details]) => (
            <div key={term}>
              <dt className="text-[1.0625rem] font-semibold">{term}</dt>
              {details.map((d) => (
                <dd key={d} className="mt-2 text-[0.96875rem] text-pretty text-muted-foreground">
                  {d}
                </dd>
              ))}
            </div>
          ))}
        </dl>
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
    <section id="coming" aria-labelledby="coming-title" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
      <div className="rounded-2xl border border-dashed border-input p-6 sm:p-10">
        <h2 id="coming-title" className="display text-[1.75rem] leading-[1.05] sm:text-[2.25rem]">
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
    <section aria-labelledby="closing-title" className="bg-primary text-primary-foreground">
      <div className="mx-auto grid max-w-6xl gap-8 px-4 py-16 sm:px-6 md:grid-cols-12 md:items-end md:py-20">
        <div className="md:col-span-8">
          <h2 id="closing-title" className="display text-[2.25rem] leading-[1] sm:text-[3rem]">
            Open your server.
          </h2>
          <p className="mt-3 max-w-[34rem] text-lg text-pretty">
            If your group already runs Modbot, pick your server on my.modbot.co and go straight to it.
          </p>
        </div>
        <div className="flex flex-wrap gap-3 md:col-span-4 md:justify-end">
          <a
            href={OPEN_MY_SERVER}
            className={cn(buttonVariants({ size: 'lg' }), 'bg-primary-foreground text-primary hover:bg-primary-foreground/90')}
          >
            Open my server
          </a>
        </div>
      </div>
    </section>
  )
}
