import { useEffect, useId, useState } from 'react'
import { ChevronDown, ExternalLink } from 'lucide-react'
import { WorldLink } from '@/components/facts'
import { EditButton, FieldRow, LanguagePicker, LinkListEditor, LongBox, SaveCancel } from '@/components/group/ProfileEditors'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { useSave } from '@/lib/useSave'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { api, type CurrentUser, type GroupInfo, type GroupProfileEdit } from '@/lib/api'
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
  readFolded,
  writeFolded,
} from '@/lib/groupProfile'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { cn } from '@/lib/utils'
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
 * Languages, Links and About can be changed in place, as on vrchat.com, by anyone who may edit the
 * group's profile: a pencil opens the card's editor, and Save sends that one field to VRChat in one
 * request. The card then shows the group as VRChat answered, with no second read.
 */
export function GroupOverview({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const { info, error, setInfo } = useGroupInfo()

  if (error) return <PageMessage tone="danger">{error}</PageMessage>
  if (!info) return <PageMessage>Loading…</PageMessage>

  const editable = can(me, 'EditGroupProfile')
  const save = (edit: GroupProfileEdit) => api.updateGroupProfile(edit).then(setInfo)

  return (
    <>
      <GroupHeader info={info} me={me} pathOf={pathOf} />

      <PanelGrid className="grid-cols-1">
        {can(me, 'ViewCalendar') && <UpcomingEvent me={me} pathOf={pathOf} />}

        <PanelGrid className="md:grid-cols-2">
          <Languages info={info} onSave={editable ? save : undefined} />
          <Links info={info} onSave={editable ? save : undefined} />
        </PanelGrid>

        <About info={info} onSave={editable ? save : undefined} />
      </PanelGrid>
    </>
  )
}

/** The next event on Modbot's calendar, or a way to make one. */
function UpcomingEvent({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [next, setNext] = useState<NextEvent | null | undefined>(undefined)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false

    // From a day back, so an event under way is still found, to the furthest the calendar answers.
    const from = new Date(Date.now() - 86_400_000)
    const to = new Date(from.getTime() + 61 * 86_400_000)

    calendarApi
      .view(from, to)
      .then((view) => {
        if (!cancelled) setNext(nextEvent(view.events, view.now))
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })

    return () => {
      cancelled = true
    }
  }, [])

  const calendar = pathOf('calendar')

  return (
    <Card>
      <CardHeader>
        <CardTitle>Upcoming event</CardTitle>
      </CardHeader>

      {failed ? (
        <EmptyRow tone="danger">Could not load the calendar.</EmptyRow>
      ) : next === undefined ? (
        <EmptyRow>Loading…</EmptyRow>
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
  const { saving, problem, run, clear } = useSave()

  const close = () => {
    setDraft(null)
    clear()
  }

  const changes = draft ? profileChanges(info, { ...draftFrom(info), languages: draft }) : {}
  const invalid = draft ? languagesProblem(draft) : null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Languages</CardTitle>
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
  const { saving, problem, run, clear } = useSave()
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
        <CardTitle>Links</CardTitle>
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

/** The browser's own store, or none when it refuses to hand one over (a private window, say). */
function localStore(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

/**
 * The description and the rules, under a heading that folds them away. The fold is remembered per
 * browser and starts unfolded; the pencil is there whenever the card is open.
 */
function About({ info, onSave }: { info: GroupInfo; onSave: OnSave }) {
  const [folded, setFolded] = useState(() => readFolded(localStore()))
  const [draft, setDraft] = useState<{ description: string; rules: string } | null>(null)
  const { saving, problem, run, clear } = useSave()
  const bodyId = useId()

  const toggle = () => {
    const next = !folded
    setFolded(next)
    writeFolded(localStore(), next)
  }

  const close = () => {
    setDraft(null)
    clear()
  }

  const { description, rules } = info
  const changes = draft ? profileChanges(info, { ...draftFrom(info), ...draft }) : {}
  const tooLong = draft ? descriptionProblem(draft.description) : null

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <h3>
            <button
              type="button"
              aria-expanded={!folded}
              aria-controls={bodyId}
              onClick={toggle}
              className="-mx-1 flex min-h-(--control-h) items-center gap-1.5 rounded-xs px-1 text-left focus-visible:outline-2 focus-visible:outline-ring"
            >
              <ChevronDown aria-hidden className={cn('size-[1.1em] shrink-0 transition-transform', folded && '-rotate-90')} />
              About this group
            </button>
          </h3>
        </CardTitle>
        {onSave && !folded && !draft && (
          <CardAction>
            <EditButton
              label="Edit description and rules"
              onClick={() => setDraft({ description: description ?? '', rules: rules ?? '' })}
            />
          </CardAction>
        )}
      </CardHeader>

      <div id={bodyId} hidden={folded}>
        {draft ? (
          <CardContent className="flex flex-col gap-3">
            <FieldRow
              label="Description"
              problem={tooLong}
              count={`${draft.description.trim().length}/${LIMITS.descriptionMax}`}
            >
              {(id) => (
                <LongBox
                  id={id}
                  value={draft.description}
                  invalid={tooLong !== null}
                  onChange={(v) => setDraft({ ...draft, description: v })}
                />
              )}
            </FieldRow>
            <FieldRow label="Rules">
              {(id) => <LongBox id={id} rows={8} value={draft.rules} onChange={(v) => setDraft({ ...draft, rules: v })} />}
            </FieldRow>
            <SaveCancel
              saving={saving}
              disabled={isEmptyEdit(changes) || tooLong !== null}
              problem={problem}
              onCancel={close}
              onSave={() => void run(() => onSave!(changes)).then((ok) => ok && close())}
            />
          </CardContent>
        ) : !description && !rules ? (
          <EmptyRow>None added</EmptyRow>
        ) : (
          <CardContent className="flex flex-col gap-3">
            {description && <p className="break-words whitespace-pre-wrap">{description}</p>}
            {rules && (
              <div className="flex flex-col gap-1">
                <h4 className="font-label">Rules</h4>
                <p className="break-words whitespace-pre-wrap">{rules}</p>
              </div>
            )}
          </CardContent>
        )}
      </div>
    </Card>
  )
}
