import { buttonVariants } from '@/components/ui/button'
import { Page } from '@/components/Site'
import { DOCS, OPEN_MY_SERVER, PAGES } from '@/lib/links'
import { cn } from '@/lib/utils'
import { AppMock } from '@/mock/AppMock'

/*
 * Every claim on this page is something Modbot does today. When a feature changes, change its words
 * here in the same commit. The words follow the brand design (.agent/specs/2026-09-16-brand-design.md
 * §6): plain, specific, no superlatives.
 */

/** @param privacy Whether the privacy policy was built, for the footer link. */
export function Home({ privacy = false }: { privacy?: boolean }) {
  return (
    <Page page="home" privacy={privacy}>
      <Hero />
      <Tools />
      <Compare />
      <Closing />
    </Page>
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
              The open moderation & analytics engine for VRChat & Discord.
            </h1>
            <p className="mt-6 max-w-[36rem] text-lg text-pretty text-muted-foreground sm:text-xl">
              One container, one database, on a server you control.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              <a href={PAGES.selfHost} className={buttonVariants({ size: 'lg' })}>
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
              <li>Web, Discord, Windows</li>
              <li>Not affiliated with VRChat</li>
            </ul>
          </div>
          {/* The full mascot, at a quarter of the hero and no more: the product is the hero. */}
          <div aria-hidden="true" className="order-first lg:order-none lg:col-span-4">
            <div className="grid place-items-center lg:aspect-square lg:max-w-[20rem]">
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
            Names and worlds are made up. Click a name, a world or an instance number.
          </p>
        </div>
      </div>
    </section>
  )
}

/**
 * Every tile is a link into the features page, so a visitor reaches the detail from whichever tool
 * caught them. Tiles renders a description list, which cannot wrap anchors, so the grid is written
 * here and copies its look.
 */
function Tools() {
  // The third part is the feature's own place on /features, so a tile lands on what it named.
  const tools: [string, string, string][] = [
    ['Live instances', 'Every open instance, who is inside, and who arrived last.', 'live'],
    ['Case files', 'Pick the reasons, write it up, attach screenshots and clips.', 'case-files'],
    ['Discord bot', 'Instance cards, mod actions in your channels, and /lookup.', 'discord-bot'],
    ['Companion', 'Reports who comes and goes from a moderator’s own PC.', 'client'],
    ['AI help', 'Chat, name and bio flags, and scheduled summaries, with a provider you choose.', 'ai'],
    ['Analytics', 'Members, mod activity, world traffic and instance hours.', 'analytics'],
  ]

  return (
    <section id="tools" aria-labelledby="tools-title" className="mx-auto max-w-6xl px-4 pt-20 sm:px-6 md:pt-28">
      <h2 id="tools-title" className="display max-w-[16ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem]">
        What Modbot does.
      </h2>
      <div className="mt-10 grid gap-px overflow-hidden rounded-xl border bg-border sm:grid-cols-2 lg:grid-cols-3">
        {tools.map(([title, line, anchor]) => (
          <a
            key={title}
            href={`${PAGES.features}#${anchor}`}
            className="bg-card p-5 outline-none transition-colors hover:bg-secondary focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:ring-inset"
          >
            <span className="block font-semibold">{title}</span>
            <span className="mt-1.5 block text-[0.9375rem] text-pretty text-muted-foreground">{line}</span>
          </a>
        ))}
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
    ['Ban list', 'Names only, no search, no reason stored.', 'Search by name or id, with a case file for each ban.'],
    ['Audit log', 'About a month of events, no filters.', 'Every event, with its data, kept as long as you choose.'],
    ['Who did what', 'The moderator is not kept with the action.', 'Each action carries who took it.'],
    ['Who was in the instance', 'Only while you are inside it yourself.', 'Reported by a moderator’s companion, kept by Modbot.'],
  ]

  return (
    <section id="compare" aria-labelledby="compare-title" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
      <div className="grid gap-5 lg:grid-cols-12 lg:items-end">
        <h2 id="compare-title" className="display max-w-[18ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem] lg:col-span-6">
          Compared with VRChat&rsquo;s group tools.
        </h2>
        <p className="max-w-[34rem] text-lg text-pretty text-muted-foreground lg:col-span-6">
          Four records a mod team asks for, and how far each tool goes.
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
          <a
            href={DOCS}
            className={cn(
              buttonVariants({ variant: 'outline', size: 'lg' }),
              'border-primary-foreground/40 bg-transparent text-primary-foreground hover:bg-primary-foreground/10 hover:text-primary-foreground',
            )}
          >
            Read the docs
          </a>
        </div>
      </div>
    </section>
  )
}
