import { useEffect, useState, type ReactNode } from 'react'
import { ExternalLink } from 'lucide-react'
import { WorldLink } from '@/components/facts'
import { InstanceTile } from '@/components/InstanceCards'
import { EditButton, FieldRow, LanguagePicker, LinkListEditor, LongBox, SaveCancel } from '@/components/group/ProfileEditors'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { useSave } from '@/lib/useSave'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { api, type CurrentUser, type GroupInfo, type GroupProfileEdit, type LiveInstance } from '@/lib/api'
import { calendarApi } from '@/lib/calendar'
import { whenRange } from '@/lib/format'
import { isWebLink, languageName, linkLabel, nextEvent, type NextEvent } from '@/lib/groupOverview'
import {
  LIMITS,
  descriptionProblem,
  isEmptyEdit,
  languagesProblem,
  linksProblem,
  profileChanges,
  draftFrom,
} from '@/lib/groupProfile'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { useGroupInfo } from '@/lib/useGroupInfo'
import { GroupHeader } from './GroupHeader'
import { PageMessage } from './shared'

/**
 * The VRChat page's Overview, laid out the way the group's own page on vrchat.com is: the header
 * (`GroupHeader`), then the overview cards.
 *
 * Everything about the group comes from `/api/analytics/group/info`, which reads what the
 * group-info sync already keeps, so opening the page asks VRChat nothing. The upcoming event is
 * Modbot's own calendar.
 *
 * Right now lists the group's open instances from what Modbot keeps, so it asks VRChat nothing either.
 *
 * Languages, Links, About and Rules can be changed in place, as on vrchat.com, by anyone who may edit the
 * group's profile: a pencil opens the card's editor, and Save sends that one field to VRChat in one
 * request. The card then shows the group as VRChat answered, with no second read.
 */
export function GroupOverview({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const { info, error, setInfo } = useGroupInfo()

  if (error) return <PageMessage tone="danger">{error}</PageMessage>
  if (!info) return <PageMessage tone="loading" />

  const editable = can(me, 'EditGroupProfile')
  const save = (edit: GroupProfileEdit) => api.updateGroupProfile(edit).then(setInfo)

  return (
    <>
      <GroupHeader info={info} me={me} pathOf={pathOf} />

      <PanelGrid className="grid-cols-1">
        {can(me, 'ViewLiveInstances') && <RightNow />}

        {can(me, 'ViewCalendar') && <UpcomingEvent me={me} pathOf={pathOf} />}

        <PanelGrid className="md:grid-cols-2">
          <Languages info={info} onSave={editable ? save : undefined} />
          <Links info={info} onSave={editable ? save : undefined} />
        </PanelGrid>

        <PanelGrid className="md:grid-cols-2">
          <About info={info} onSave={editable ? save : undefined} />
          <Rules info={info} onSave={editable ? save : undefined} />
        </PanelGrid>
      </PanelGrid>
    </>
  )
}

/**
 * A card's title as vrchat.com sets it on a group's page: large and bold ("About This Group",
 * "Rules"), not the console's small panel label. The owner chose this on 2026-09-28 so the VRChat
 * page reads like VRChat's; it is 1.4 times the body size, so it grows with the density as the
 * label would. VRChat's teal and its own face were not taken: the colour and the face stay Modbot's.
 */
function GroupCardTitle({ children }: { children: ReactNode }) {
  return (
    <CardTitle style={{ fontSize: 'calc(var(--text-base) * 1.4)', fontWeight: 700 }}>{children}</CardTitle>
  )
}

/**
 * The group's instances open right now, as tiles the way the game draws them, each opening its
 * instance. Read once from what Modbot already keeps (`/api/live`), so it asks VRChat nothing.
 */
function RightNow() {
  const [open, setOpen] = useState<LiveInstance[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .live()
      .then((view) => {
        if (!cancelled) setOpen(view.instances)
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })

    return () => {
      cancelled = true
    }
  }, [tries])

  return (
    <Card>
      <CardHeader>
        <GroupCardTitle>Right now</GroupCardTitle>
      </CardHeader>

      {failed ? (
        <EmptyRow
          tone="danger"
          onTryAgain={() => {
            setFailed(false)
            setTries((n) => n + 1)
          }}
        >
          Could not load the open instances.
        </EmptyRow>
      ) : open === null ? (
        <EmptyRow tone="loading" />
      ) : open.length === 0 ? (
        <EmptyRow>No group instances open</EmptyRow>
      ) : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(11rem,1fr))] gap-3 p-3">
          {open.map((instance) => (
            <InstanceTile
              key={instance.id}
              instanceId={instance.id}
              worldName={instance.worldName}
              instanceName={instance.instanceName}
              number={instance.vrChatInstanceId}
              imageUrl={instance.worldImageUrl}
              people={instance.headCount}
              peopleUnsure={instance.headCountUnsure}
              capacity={instance.worldCapacity}
              groupAccessType={instance.groupAccessType}
              region={instance.region}
              platforms={instance.worldPlatforms}
            />
          ))}
        </div>
      )}
    </Card>
  )
}

/** The next event on Modbot's calendar, or a way to make one. */
function UpcomingEvent({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [next, setNext] = useState<NextEvent | null | undefined>(undefined)
  const [failed, setFailed] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    // From a day back, so an event under way is still found, to the furthest the calendar answers.
    const from = new Date(Date.now() - 86_400_000)
    const to = new Date(from.getTime() + 61 * 86_400_000)

    const show = () =>
      calendarApi
        .view(from, to)
        .then((view) => {
          if (!cancelled) setNext(nextEvent(view.events, view.now))
        })
        .catch(() => {
          if (!cancelled) setFailed(true)
        })

    // What Modbot has now, then again once VRChat's own calendar has been read for the month the
    // next event is in (calendar design §12.1): an event made on vrchat.com shows up here too.
    void show()
    calendarApi
      .readVRChat({ upcoming: true })
      .then((read) => {
        if (!cancelled && read.outcome === 'read') void show()
      })
      .catch(() => {
        // Modbot's own calendar is still shown; the calendar page says what went wrong.
      })

    return () => {
      cancelled = true
    }
  }, [tries])

  const calendar = pathOf('calendar')

  return (
    <Card>
      <CardHeader>
        <GroupCardTitle>Upcoming event</GroupCardTitle>
      </CardHeader>

      {failed ? (
        <EmptyRow
          tone="danger"
          onTryAgain={() => {
            setFailed(false)
            setNext(undefined)
            setTries((n) => n + 1)
          }}
        >
          Could not load the calendar.
        </EmptyRow>
      ) : next === undefined ? (
        <EmptyRow tone="loading" />
      ) : next === null ? (
        <div className="flex flex-wrap items-center justify-between gap-x-3 pr-(--panel-pad)">
          <EmptyRow>No upcoming events</EmptyRow>
          {can(me, 'ManageCalendar') && (
            <Button size="sm" asChild>
              <a href={`${calendar}?new=1`} onClick={followLink(`${calendar}?new=1`)}>
                Create event
              </a>
            </Button>
          )}
        </div>
      ) : (
        <EventRow next={next} href={`${calendar}?event=${encodeURIComponent(next.event.id)}`} />
      )}
    </Card>
  )
}

function EventRow({ next, href }: { next: NextEvent; href: string }) {
  const { event } = next
  const picture = vrchatMedia(event.imageUrl ?? event.worldThumbnailUrl)

  return (
    <div className="flex gap-3 p-(--panel-pad)">
      {picture && (
        <img
          src={picture}
          alt=""
          referrerPolicy="no-referrer"
          className="aspect-video w-28 shrink-0 self-start border border-(length:--hairline) bg-muted object-cover sm:w-40"
        />
      )}

      <div className="flex min-w-0 flex-col gap-1">
        <a href={href} onClick={followLink(href)} className="font-medium break-words hover:underline">
          {event.title}
        </a>
        <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {whenRange(next.startsAt, next.endsAt)}
        </span>
        {event.worldId && (
          <span style={{ fontSize: 'var(--text-small)' }}>
            <WorldLink id={event.worldId} name={event.worldName} unnamed="id" />
          </span>
        )}
      </div>
    </div>
  )
}

/** A card's Save: the edit, sent; undefined when this person may not change the group. */
type OnSave = ((edit: GroupProfileEdit) => Promise<unknown>) | undefined

function Languages({ info, onSave }: { info: GroupInfo; onSave: OnSave }) {
  const [draft, setDraft] = useState<string[] | null>(null)
  const { saving, problem, missing, run, clear } = useSave()

  const close = () => {
    setDraft(null)
    clear()
  }

  const changes = draft ? profileChanges(info, { ...draftFrom(info), languages: draft }) : {}
  const invalid = draft ? languagesProblem(draft) : null

  return (
    <Card>
      <CardHeader>
        <GroupCardTitle>Languages</GroupCardTitle>
        {onSave && !draft && (
          <CardAction>
            <EditButton label="Edit languages" onClick={() => setDraft([...info.languages])} />
          </CardAction>
        )}
      </CardHeader>

      {draft ? (
        <CardContent className="flex flex-col gap-3">
          <LanguagePicker value={draft} onChange={setDraft} />
          <SaveCancel
            saving={saving}
            missing={missing}
            disabled={isEmptyEdit(changes) || invalid !== null}
            problem={problem ?? invalid}
            onCancel={close}
            onSave={() => void run(() => onSave!(changes)).then((ok) => ok && close())}
          />
        </CardContent>
      ) : info.languages.length === 0 ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <CardContent className="flex flex-wrap gap-1.5">
          {info.languages.map((code) => (
            <Badge key={code} variant="secondary" title={code}>
              {languageName(code)}
            </Badge>
          ))}
        </CardContent>
      )}
    </Card>
  )
}

function Links({ info, onSave }: { info: GroupInfo; onSave: OnSave }) {
  const [draft, setDraft] = useState<string[] | null>(null)
  const { saving, problem, missing, run, clear } = useSave()
  const links = info.links.filter(isWebLink)

  const close = () => {
    setDraft(null)
    clear()
  }

  const changes = draft ? profileChanges(info, { ...draftFrom(info), links: draft }) : {}
  const invalid = draft ? linksProblem(draft) : null

  return (
    <Card>
      <CardHeader>
        <GroupCardTitle>Links</GroupCardTitle>
        {onSave && !draft && (
          <CardAction>
            <EditButton label="Edit links" onClick={() => setDraft(info.links.length > 0 ? [...info.links] : [''])} />
          </CardAction>
        )}
      </CardHeader>

      {draft ? (
        <CardContent className="flex flex-col gap-3">
          <LinkListEditor value={draft} onChange={setDraft} />
          <SaveCancel
            saving={saving}
            missing={missing}
            disabled={isEmptyEdit(changes) || invalid !== null}
            problem={problem ?? invalid}
            onCancel={close}
            onSave={() => void run(() => onSave!(changes)).then((ok) => ok && close())}
          />
        </CardContent>
      ) : links.length === 0 ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <ul className="divide-y-(--hairline) divide-border">
          {links.map((url) => (
            <li key={url}>
              <a
                href={url}
                target="_blank"
                rel="noopener noreferrer"
                title={url}
                className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) text-link hover:underline"
              >
                <ExternalLink aria-hidden className="size-[1em] shrink-0" />
                <span className="truncate">{linkLabel(url)}</span>
              </a>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

/**
 * The description, as vrchat.com's About This Group card shows it, with a pencil of its own.
 * Rules sit in the card beside it, as they do there.
 */
function About({ info, onSave }: { info: GroupInfo; onSave: OnSave }) {
  const [draft, setDraft] = useState<string | null>(null)
  const { saving, problem, missing, run, clear } = useSave()

  const close = () => {
    setDraft(null)
    clear()
  }

  const changes = draft !== null ? profileChanges(info, { ...draftFrom(info), description: draft }) : {}
  const tooLong = draft !== null ? descriptionProblem(draft) : null

  return (
    <Card>
      <CardHeader>
        <GroupCardTitle>About this group</GroupCardTitle>
        {onSave && draft === null && (
          <CardAction>
            <EditButton label="Edit description" onClick={() => setDraft(info.description ?? '')} />
          </CardAction>
        )}
      </CardHeader>

      {draft !== null ? (
        <CardContent className="flex flex-col gap-3">
          <FieldRow label="Description" problem={tooLong} count={`${draft.trim().length}/${LIMITS.descriptionMax}`}>
            {(id) => <LongBox id={id} value={draft} invalid={tooLong !== null} onChange={setDraft} />}
          </FieldRow>
          <SaveCancel
            saving={saving}
            missing={missing}
            disabled={isEmptyEdit(changes) || tooLong !== null}
            problem={problem}
            onCancel={close}
            onSave={() => void run(() => onSave!(changes)).then((ok) => ok && close())}
          />
        </CardContent>
      ) : !info.description ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <CardContent>
          <p className="break-words whitespace-pre-wrap">{info.description}</p>
        </CardContent>
      )}
    </Card>
  )
}

/** The group's rules, in a card of their own beside About, as on vrchat.com. */
function Rules({ info, onSave }: { info: GroupInfo; onSave: OnSave }) {
  const [draft, setDraft] = useState<string | null>(null)
  const { saving, problem, missing, run, clear } = useSave()

  const close = () => {
    setDraft(null)
    clear()
  }

  const changes = draft !== null ? profileChanges(info, { ...draftFrom(info), rules: draft }) : {}

  return (
    <Card>
      <CardHeader>
        <GroupCardTitle>Rules</GroupCardTitle>
        {onSave && draft === null && (
          <CardAction>
            <EditButton label="Edit rules" onClick={() => setDraft(info.rules ?? '')} />
          </CardAction>
        )}
      </CardHeader>

      {draft !== null ? (
        <CardContent className="flex flex-col gap-3">
          <FieldRow label="Rules">{(id) => <LongBox id={id} rows={8} value={draft} onChange={setDraft} />}</FieldRow>
          <SaveCancel
            saving={saving}
            missing={missing}
            disabled={isEmptyEdit(changes)}
            problem={problem}
            onCancel={close}
            onSave={() => void run(() => onSave!(changes)).then((ok) => ok && close())}
          />
        </CardContent>
      ) : !info.rules ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <CardContent>
          <p className="break-words whitespace-pre-wrap">{info.rules}</p>
        </CardContent>
      )}
    </Card>
  )
}
