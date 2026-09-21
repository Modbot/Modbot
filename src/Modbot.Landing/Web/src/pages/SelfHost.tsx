import { Page } from '@/components/Site'
import { DEPLOY_ON_RAILWAY, INSTALL_SCRIPT, SELF_HOSTING_GUIDE } from '@/lib/links'

/**
 * The page a head moderator reads before deciding to run Modbot: where the records live, what the
 * server talks to, what it does with the VRChat account it is given, and what is not written yet.
 * Every claim here is checked against the product, so change the words in the same commit as the
 * behaviour they describe.
 */
export function SelfHost({ privacy = false }: { privacy?: boolean }) {
  return (
    <Page page="selfHost" privacy={privacy}>
      <Server />
      <Rules />
      <Coming />
    </Page>
  )
}

function Server() {
  const facts = [
    'Every record the server keeps is in your database. Evidence files go to S3-compatible storage, a mounted folder or that same database.',
    'The server talks to VRChat, Discord, your mail server and an AI provider when you set each one up. It never contacts modbot.co.',
    'The companion also sends events to Modbot Cloud as a backup. This is on by default, and it has a switch for it.',
    'Usage reports have no field for your group or its members, and you can turn them off.',
  ]

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
          <h1 id="self-host-title" className="display text-[2.5rem] leading-[1] sm:text-[3.5rem]">
            Runs on your own server.
          </h1>
          <p className="mt-5 max-w-[34rem] text-lg text-pretty text-muted-foreground">
            One container and one PostgreSQL database. Your group&rsquo;s records are stored there.
          </p>
          <h2 className="mt-8 text-[1.0625rem] font-semibold">Where your data goes.</h2>
          <ul className="mt-3 max-w-[34rem] border-t">
            {facts.map((fact) => (
              <li key={fact} className="border-b py-2.5 text-[0.96875rem] text-pretty">
                {fact}
              </li>
            ))}
          </ul>
        </div>

        <div className="flex min-w-0 flex-col gap-4 lg:col-span-6">
          {/*
           * These blocks are pictures of a terminal, so they keep their own dark colours in both
           * themes and do not use the site's tokens.
           *
           * The address in the command is the link to the script itself. Somebody piping a script
           * to a shell should be able to read it first, and making the address they are about to
           * run the thing they click says so without a sentence saying so.
           */}
          <div className="window-shadow overflow-hidden rounded-xl border border-[#2a2738] bg-[#0f0e15] text-[#e6e6eb]">
            <div className="border-b border-[#2a2738] px-4 py-2.5 text-sm text-[#a5a3b8]">One command</div>
            <pre className="overflow-x-auto px-3 py-4 font-mono text-[0.625rem] leading-7 sm:px-4 sm:text-[0.875rem]">
              <code>
                <span className="text-[#a5a3b8]">$</span> curl -fsSL{' '}
                <a
                  href={INSTALL_SCRIPT}
                  className="text-[#b6acff] underline underline-offset-4 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                >
                  https://modbot.co/get.sh
                </a>{' '}
                | sh
              </code>
            </pre>
          </div>

          <div className="window-shadow overflow-hidden rounded-xl border border-[#2a2738] bg-[#0f0e15] text-[#e6e6eb]">
            <div className="border-b border-[#2a2738] px-4 py-2.5 text-sm text-[#a5a3b8]">The one setting</div>
            <pre className="overflow-x-auto px-3 py-4 font-mono text-[0.625rem] leading-7 sm:px-4 sm:text-[0.875rem]">
              <code>
                <span className="text-[#b6acff]">DATABASE_URL</span>=postgres://user:password@host:5432/modbot{'\n'}
                <span className="text-[#a5a3b8]"># PORT is optional and defaults to 8080</span>
              </code>
            </pre>
          </div>
          <div className="rounded-xl border bg-card p-5">
            <h3 className="font-semibold">Everything else is set up in your browser.</h3>
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

          {/*
            Railway's own button, served from this site rather than from theirs: the page makes no
            third-party request, which is the same promise the rest of this section makes. Its size
            is the asset's own 183x40, so it is never scaled.
          */}
          <a
            href={DEPLOY_ON_RAILWAY}
            className="inline-flex w-fit rounded-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
          >
            <img src="/railway-button.svg" alt="Deploy on Railway" width={183} height={40} />
          </a>
        </div>
      </div>
    </section>
  )
}

/** The facts a head moderator checks before handing a tool a VRChat login. Sources: docs/security.md, docs/discord-bot.md. */
function Rules() {
  const items: [string, string][] = [
    [
      'The VRChat client',
      'Modbot does not modify the game. The server reads VRChat’s API and the companion reads the log file VRChat writes.',
    ],
    [
      'The VRChat account',
      'Modbot signs in as one account you give it, which must be a moderator of the group. Its password and two-factor secret are stored encrypted in your database.',
    ],
    [
      'VRChat’s API',
      'VRChat offers no support for third-party use of its API. Modbot is independent and not affiliated with VRChat Inc.',
    ],
    [
      'The Discord bot’s permissions',
      'View Channels, Send Messages, Embed Links, Read Message History and View Audit Log. It does not ask for Administrator.',
    ],
  ]

  return (
    <section id="rules" aria-labelledby="rules-title" className="border-y bg-card">
      <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
        <h2 id="rules-title" className="display max-w-[18ch] text-[2.25rem] leading-[1.04] sm:text-[2.875rem]">
          Modbot and VRChat&rsquo;s rules.
        </h2>
        <dl className="mt-10 grid gap-x-12 gap-y-8 md:grid-cols-2">
          {items.map(([term, detail]) => (
            <div key={term}>
              <dt className="text-[1.0625rem] font-semibold">{term}</dt>
              <dd className="mt-2 text-[0.96875rem] text-pretty text-muted-foreground">{detail}</dd>
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
    ['Discord sync', 'Linked accounts, roles and bans kept in step with the group.'],
    ['Segments and giveaways', 'Find “regulars with 10+ hours this month and no bans”, then draw fairly.'],
    ['Shared warnings', 'Flags from the groups someone belongs to, shared between groups.'],
  ]

  return (
    <section id="coming" aria-labelledby="coming-title" className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
      {/* The dashed border keeps planned work from reading as something a group can turn on today. */}
      <div className="rounded-xl border border-dashed border-input p-6 sm:p-10">
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
