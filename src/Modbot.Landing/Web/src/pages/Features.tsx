import { Feature, Page, Tiles } from '@/components/Site'
import { AppMock } from '@/mock/AppMock'
import { AnalyticsPanel } from '@/visuals/AnalyticsPanel'
import { CaseFileMock } from '@/visuals/CaseFileMock'
import { ChatMock } from '@/visuals/ChatMock'
import { DiscordCards } from '@/visuals/DiscordCards'
import { OverlayMock } from '@/visuals/OverlayMock'

/*
 * Every claim on this page is something Modbot does today; the planned work lives on /self-host under
 * "Not built yet". When a feature changes, change its words here in the same commit. The words follow
 * the brand design (.agent/specs/2026-09-16-brand-design.md §6): plain, specific, no superlatives.
 */

/** @param privacy Whether the privacy policy was built, for the footer link. */
export function Features({ privacy = false }: { privacy?: boolean }) {
  return (
    <Page page="features" privacy={privacy}>
      <section aria-labelledby="features-title" className="mx-auto max-w-6xl px-4 pt-14 sm:px-6 md:pt-20">
        <div className="grid gap-5 lg:grid-cols-12 lg:items-end">
          {/* The home page already asks "What Modbot does."; this page is the answer, so it takes the
              name the header and the title use rather than repeating the question. */}
          <h1 id="features-title" className="display max-w-[18ch] text-[2.5rem] leading-[1] sm:text-[3.5rem] lg:col-span-6">
            Features.
          </h1>
          <p className="max-w-[34rem] text-lg text-pretty text-muted-foreground lg:col-span-6">
            Six tools for running a VRChat group, and the everyday parts underneath them.
          </p>
        </div>
      </section>

      {/* The visuals alternate sides so the page reads as a zigzag rather than six identical rows. */}
      <Feature
        id="live"
        sources={['VRChat', 'Client', 'Modbot']}
        title="Every instance, as it happens."
        lead="The Live page shows every instance the group has open, how full it is, and who arrived last."
        facts={[
          'Each group instance appears as it opens, with VRChat’s own head count.',
          'While a moderator’s companion is in the instance, you see each person.',
          'People with earlier mod actions stand out in the list.',
          'Refreshes every five seconds and stops while its tab is hidden.',
        ]}
        // Opened on a person, because the popups are the part of the page words cannot show.
        visual={
          <AppMock
            label="Sample of Modbot's Live page, with a person opened over it."
            startWith={[{ kind: 'world', id: 'wrld_harbor' }, { kind: 'person', id: 'usr_teaspoon' }]}
            play={false}
            className="h-[34rem]"
          />
        }
      />

      <Feature
        id="discord-bot"
        flip
        sources={['Discord']}
        title="Instance cards in your Discord."
        lead="The bot posts a card for each group instance and updates it as people come and go."
        facts={[
          'Send bans, kicks, joins, role changes and case files to channels you choose.',
          <><code className="font-mono text-[0.9em]">/lookup</code> a person or <code className="font-mono text-[0.9em]">/recent</code> actions, with replies only you can see.</>,
          'Names appear on a card only while a moderator is watching.',
          'Commands follow the same permissions as the web app.',
        ]}
        visual={<DiscordCards />}
      />

      <Feature
        id="case-files"
        sources={['Modbot']}
        title="Every ban gets a case file."
        lead="Pick the reasons, write it up, attach screenshots and clips."
        facts={[
          'PNG, JPEG, WebP, GIF, MP4 and WebM, checked by contents.',
          'Stored in S3-compatible storage, a mounted folder or PostgreSQL.',
          'Every edit is kept. A withdrawn case file stays on record.',
          'The Bans page counts recent bans still waiting for one.',
        ]}
        visual={<CaseFileMock />}
      />

      <Feature
        id="client"
        flip
        sources={['Client']}
        title="Who is in the instance, from a moderator’s own PC."
        lead="The companion reads VRChat’s log as you play and reports who comes and goes."
        facts={[
          'Windows 10 and 11, no administrator rights needed.',
          'Pairs with your server through my.modbot.co using a one-time link.',
          'Its token can only report presence and read the instance list.',
          'In SteamVR, an overlay lists the instance and warns about people with a record.',
        ]}
        visual={<OverlayMock />}
      />

      <Feature
        id="ai"
        sources={['VRChat', 'Modbot']}
        title="AI help, with a provider you choose."
        lead="Connect a provider. Nothing goes to it until you do."
        facts={[
          <><strong className="font-semibold">Chat:</strong> ask about a person, a world or the group, using the asker&rsquo;s own permissions.</>,
          <><strong className="font-semibold">Flags:</strong> names and bios checked against term lists and AI topics. A moderator reviews every flag.</>,
          <><strong className="font-semibold">Insights:</strong> scheduled summaries of your group&rsquo;s figures.</>,
          'A daily call limit for moderation, and every call’s usage recorded.',
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
          <><strong className="font-semibold">My Group:</strong> members, joins, leaves, invites, how long people stay.</>,
          <><strong className="font-semibold">My Team:</strong> actions per moderator, and when people stayed with no moderator present.</>,
          <><strong className="font-semibold">Worlds:</strong> where your people spend their time.</>,
          <><strong className="font-semibold">Instances:</strong> how long they run, and the busiest hours in your time zone.</>,
        ]}
        visual={<AnalyticsPanel />}
      />

      <Everyday />
    </Page>
  )
}

/** The parts that carry no visual of their own, so they are one card section instead of six features. */
function Everyday() {
  const items: [string, string][] = [
    ['Audit log', 'One timeline from VRChat, the syncs, the client and Modbot itself.'],
    ['Members and bans', 'Search by name or id, filter by role, see who left and when.'],
    ['Roles', 'Administrator, Moderator and Viewer, or your own roles from 21 permissions.'],
    ['Staff accounts', 'Every moderator signs in as themselves, verified by a code in their bio.'],
    ['Reviews', 'Repeat offenders counted over 30 days, and a review when a moderator’s actions look unusual.'],
    ['Your own tools', 'API keys with a person’s permissions, and new events over a live WebSocket.'],
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
