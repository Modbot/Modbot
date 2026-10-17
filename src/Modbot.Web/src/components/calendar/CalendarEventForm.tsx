import { useEffect, useMemo, useRef, useState } from 'react'
import { Check, ChevronDown, ChevronRight, Link2, Plus } from 'lucide-react'
import { DropdownMenu, Popover } from 'radix-ui'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { Checkbox, Field, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { DiscardChanges } from './DiscardChanges'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Tabs } from '@/components/ui/tabs'
import { ApiError } from '@/lib/api'
import {
  blankEvent,
  blueskyVideoAddress,
  calendarApi,
  CALENDAR_COVER_MAX_BYTES,
  CATEGORY_LABEL,
  coverAddress,
  DAYS,
  inputFrom,
  PLATFORM_LABEL,
  type BlueskyPicture,
  type BlueskyGif,
  type BlueskyVideo,
  type CalendarEvent,
  type CalendarEventInput,
  type CalendarInviteChoices,
  type CalendarRepeat,
} from '@/lib/calendar'
import {
  BLUESKY_BEFORE_CHOICES,
  DEFAULT_BLUESKY_BEFORE,
  DESTINATION_LABEL,
  DESTINATION_SWITCH,
  DESTINATIONS,
  googleChip,
  googleTicked,
  isSetUp,
  SERVICE_TABS,
  SET_UP_LINK,
  TAB_LABEL,
  tabsFor,
  type CalendarDestination,
  type CalendarReady,
  type CalendarServiceTab,
  type CalendarTab,
} from '@/lib/calendarPlaces'
import { calendarTemplateApi } from '@/lib/calendarTemplateApi'
import { useCanManageSettings } from '@/lib/manageSettings'
import { AUTO_LINKS, autoLinkOf, cleanFillInName, FILL_IN_TEXTS, fillInKey, fillInMarker, fillInsIn } from '@/lib/calendarFillIns'
import { addMinutes, fieldsFromInput, joinLength, splitLength } from '@/lib/calendarTemplates'
import { countOf, OWN_KEYS, OWN_TEXT, ownKeysWithText, placeholderOf, postAround, sharedWords, textShown, type OwnKey } from '@/lib/calendarOwnText'
import { saveWarnings } from '@/lib/calendarWarnings'
import type { CardEditing, CardField } from './CardText'
import { EventPreview, usePreviewAnswer } from './EventPreview'
import { FillInText } from './FillInText'
import { NotSetUp } from './NotSetUp'
import { worldListApi, type WorldList, type WorldListWorld } from '@/lib/worldLists'
import { calendarCategoryApi, type CalendarCategoryRow } from '@/lib/calendarCategories'
import { DISCORD_COVER_ASPECT, pictureFollowingLink, VRCHAT_PICTURE_ASPECT } from '@/lib/eventPicture'
import { CroppedPicture } from './PictureCrop'
import { usePictureThumbnails } from './usePictureThumbnails'
import { CopyButton, PasteButton } from './PasteButton'
import { VRChatPreviews } from './VRChatPreviews'
import { DiscordPreviews } from './DiscordPreviews'
import { BlueskyPreviews } from './BlueskyPreviews'
import { BLUESKY_BYTE_LIMIT, BLUESKY_PICTURE_MAX_BYTES } from '@/lib/postRules'
import { BLUESKY_PICTURES_MAX, shownPictures, withoutPicture, withPictureChanged, withPictures } from '@/lib/blueskyPictures'
import { gifFromKlipy, gifWithAlt, hasGif, NO_GIF } from '@/lib/blueskyGif'
import { hasVideo, NO_VIDEO, videoFileProblem, videoFromFile, videoFromLink, videoLinkProblem, videoWithAlt } from '@/lib/blueskyVideo'
import { smallJpeg } from '@/lib/pictureFiles'
import { PlatformIcons } from './PlatformIcons'
import { useDiscordCover, useVRChatPicture, vrchatPictureAddress } from './useVRChatPicture'
import { DiscordPictureField, VRChatPictureField } from './VRChatPictureField'
import { FROM_LIST, listMissing, NO_WORLD, OTHER_WORLD, worldChoice } from '@/lib/calendarWorld'
import { PopoverPortal, MenuPortal } from '@/components/ui/dialog-portal'


/** How a repeat stops: never, after its last date, or after a number of times. */
type RepeatEnd = 'never' | 'date' | 'times'

/** How long a save waits for the look at events already at that time, before it goes on without it. */
const SAME_EVENTS_WAIT_MS = 3000

/** How long a card waits for the form to stop changing before it is drawn again. */
const PREVIEW_WAIT_MS = 400

/** No service has words of its own: a card's text is then the event's. */
const NO_OWN_TEXT: ReadonlySet<OwnKey> = new Set()
const EVERY_OWN_KEY: ReadonlySet<OwnKey> = new Set(OWN_KEYS)
const DISCORD_PARTS = ['discordEvent', 'channelPost'] as const

/** What "After a number of times" starts at. */
const FIRST_TIMES = 4

/** "week" or "weeks", for the number beside "Every". */
const REPEAT_UNIT: Record<Exclude<CalendarRepeat, 'none'>, [string, string]> = {
  daily: ['day', 'days'],
  weekly: ['week', 'weeks'],
  monthly: ['month', 'months'],
}

/**
 * The create and edit form for one event (calendar design §2, §14).
 *
 * Every place that is set up has a tab of its own, with its own fields and a card of the event the
 * way that place would show it; the event's own fields are on the Shared info tab. A place is used once
 * anything on its tab is changed (a new event starts with none), and its tab is marked; Schedule
 * then asks once, naming the places. Opening the instance is on the VRChat tab and is used once
 * its fields are changed. Places not set up are under the + after the tabs, each linking to where
 * it is set up. A place in use that cannot work as things are set up says "Not set up" on its tab;
 * Google Calendar says "Members only" for an event only members see, which it never takes (Google
 * Calendar design §3.8). The buttons are pinned under the form, so a phone never scrolls them away.
 */
export function CalendarEventForm({
  event,
  initial,
  categories,
  platforms,
  ready,
  pictureUploads = false,
  vrchatPlus = null,
  template,
  templateTitle,
  emptyPlaces = false,
  onClose,
  onSaved,
  onTemplateSaved,
}: {
  event: CalendarEvent | null
  /** What a new event starts with: a time drawn on the calendar, or a copy of another event. */
  initial?: CalendarEventInput
  categories: string[]
  platforms: string[]
  /** Which places are set up, from the calendar's own read. */
  ready?: CalendarReady | null
  /** Whether a VRChat picture may be uploaded (Settings). Off, the picture is only an id typed or cleared. */
  pictureUploads?: boolean
  /** Whether Modbot's VRChat account has VRChat+. False turns the VRChat picture off; null is not known. */
  vrchatPlus?: boolean | null
  /** Set, the form makes or edits an event template: a name and a length in place of the dates. */
  template?: { id: string | null; name: string; lengthMinutes: number }
  /** The title the event's template gave it, when it was made from one: a different title is warned about on save. */
  templateTitle?: string
  /** A new event made from nothing: it goes nowhere until a place's tab is changed. */
  emptyPlaces?: boolean
  onClose: () => void
  onSaved?: (saved: CalendarEvent) => void
  onTemplateSaved?: () => void
}) {
  const [input, setInput] = useState<CalendarEventInput>(() => {
    const first = initial ?? (event ? inputFrom(event) : blankEvent(new Date()))
    return emptyPlaces ? { ...first, ...NO_PLACES } : first
  })
  const [choices, setChoices] = useState<CalendarInviteChoices>({ staff: [], lists: null })
  const [typedWorld, setTypedWorld] = useState(false)
  const [lists, setLists] = useState<WorldList[]>([])
  // The Seen list's worlds: not loaded with the form, but when its arrow is first opened, and kept after.
  const [seenWorlds, setSeenWorlds] = useState<WorldListWorld[] | null>(null)
  const seenAsked = useRef(false)

  function loadSeenWorlds() {
    if (seenAsked.current) return
    seenAsked.current = true
    worldListApi
      .seen()
      .then((seen) => setSeenWorlds(seen.worlds))
      .catch(() => {
        // Opening the arrow again tries again.
        seenAsked.current = false
      })
  }
  // The categories made in Modbot, which any number may be ticked on the event.
  const [categoryChoices, setCategoryChoices] = useState<CalendarCategoryRow[]>([])
  // A new event starts on Pick from a list; a saved one with no world shows None.
  const [pickingList, setPickingList] = useState(() => !event && !template?.id && input.worldId === null)
  // Set once Save or Schedule was refused for a list not chosen, so the field says so.
  const [listMissed, setListMissed] = useState(false)
  // Kept as typed, so a comma can be typed; split when saving.
  const [languages, setLanguages] = useState(() => input.languages.join(', '))
  const [tags, setTags] = useState(() => input.tags.join(', '))
  const [busy, setBusy] = useState(false)
  // Fixed when the form opens: which editor this is, and whether the event is new.
  const [forTemplate] = useState(template !== undefined)
  const [startedNew] = useState(event === null)
  const [templateName, setTemplateName] = useState(template?.name ?? '')
  const [length, setLength] = useState(template?.lengthMinutes ?? 0)
  // Template editor only: the fill-ins made here and not yet in any text, and the new one's name as
  // typed. Where a fill-in or an automatic link goes (the text last clicked into) is for both editors.
  const [spareFillIns, setSpareFillIns] = useState<string[]>([])
  const [newFillIn, setNewFillIn] = useState('')
  const [putFillIn, setPutFillIn] = useState<{ insert: (name: string) => boolean } | null>(null)
  // Where a link goes: the description last clicked into. A title never holds a link.
  const [putLink, setPutLink] = useState<{ insert: (name: string) => boolean } | null>(null)
  const pictureThumbnails = usePictureThumbnails()
  // The VRChat picture being cropped or uploaded (§15.3). A crop not uploaded yet is uploaded by
  // Save or Schedule first; one on its way up holds the buttons, or the event would save without it.
  const vrchatPicture = useVRChatPicture({
    eventId: event?.id ?? null,
    thumbnails: pictureThumbnails,
    onUploaded: (fileId) => set('vrChatImageId', fileId),
    initialLink: input.imageUrl,
  })
  // The picture for Discord, cropped to Discord's cover shape and kept by Modbot (§15.4).
  const discordCover = useDiscordCover({
    onUploaded: (coverId) => set('coverPictureId', coverId),
    initialLink: input.imageUrl,
  })
  // Bluesky's pictures on their way up (one count per pick), and why the last one was not added.
  const [blueskyUploads, setBlueskyUploads] = useState(0)
  const [blueskyVideoUploads, setBlueskyVideoUploads] = useState(0)
  // What the GIF button searches with; none, and the button is not drawn.
  const [gifSearchCode, setGifSearchCode] = useState<string | null>(null)
  const [blueskyMediaError, setBlueskyMediaError] = useState<string | null>(null)
  // Where an emoji goes: the Bluesky box last clicked into.
  const [putBlueskyText, setPutBlueskyText] = useState<{ put: (words: string) => boolean } | null>(null)
  const uploading = vrchatPicture.uploading || discordCover.uploading || blueskyUploads > 0 || blueskyVideoUploads > 0
  // Everything a save was refused for, one line each, in the server's order.
  const [error, setError] = useState<string[] | null>(null)
  // What a save found worth a second look, one line each. The next save of the same form goes through.
  const [warnings, setWarnings] = useState<string[]>([])
  const [tab, setTab] = useState<CalendarTab>('event')
  // Kept apart from the input, so "On a date" stays picked while its date is still empty.
  const [repeatEnd, setRepeatEnd] = useState<RepeatEnd>(() =>
    input.repeatTimes !== null ? 'times' : input.repeatUntil !== null ? 'date' : 'never',
  )
  // What the cards draw: the form as it was when a service's tab was opened, or a moment after the last change.
  const [shown, setShown] = useState<CalendarEventInput | null>(null)
  // The own fields a service's card has been typed in (or started with words): they stay the service's
  // even while empty, so a box cleared to be retyped does not fill itself with the event's words.
  const [edited, setEdited] = useState<ReadonlySet<OwnKey>>(() => ownKeysWithText(input))
  // How the form opened, to tell whether closing it would lose something. The two fixes made once the
  // lists load below are made here too, so they are not taken for changes.
  const [opened, setOpened] = useState(() => ({ input, languages, tags, templateName, length, repeatEnd }))
  const [asking, setAsking] = useState(false)

  useEffect(() => {
    worldListApi
      .all({ seenWorlds: false })
      .then((answer) => setLists(answer.lists))
      .catch(() => setLists([]))
    calendarCategoryApi
      .all()
      .then((answer) => {
        setCategoryChoices(answer.categories)
        // A category deleted since a template or an event was loaded has nothing to tick.
        const known = new Set(answer.categories.map((c) => c.id))
        const withoutGone = (current: CalendarEventInput) =>
          current.categoryIds.every((id) => known.has(id))
            ? current
            : { ...current, categoryIds: current.categoryIds.filter((id) => known.has(id)) }
        setInput(withoutGone)
        setOpened((was) => ({ ...was, input: withoutGone(was.input) }))
      })
      .catch(() => setCategoryChoices([]))
    calendarApi
      .inviteChoices(forTemplate)
      .then((answer) => {
        setChoices(answer)
        // A new event cannot start with a list its maker may not pick (a template's, say); the
        // server would refuse it and the picker is locked.
        if (answer.lists === null && !forTemplate && startedNew) {
          const withoutList = (current: CalendarEventInput) =>
            current.inviteListId === null ? current : { ...current, inviteListId: null }
          setInput(withoutList)
          setOpened((was) => ({ ...was, input: withoutList(was.input) }))
        }
      })
      .catch(() => setChoices({ staff: [], lists: null }))
    calendarApi
      .gifKey()
      .then((answer) => setGifSearchCode(answer.key))
      .catch(() => setGifSearchCode(null))
  }, [forTemplate, startedNew])

  const zones = useMemo(() => {
    try {
      return Intl.supportedValuesOf('timeZone')
    } catch {
      return ['UTC']
    }
  }, [])

  const set = <K extends keyof CalendarEventInput>(key: K, value: CalendarEventInput[K]) =>
    setInput((current) => ({ ...current, [key]: value }))

  const toggle = (key: 'repeatDays' | 'platforms' | 'categoryIds', value: string) =>
    setInput((current) => ({
      ...current,
      [key]: current[key].includes(value) ? current[key].filter((v) => v !== value) : [...current[key], value],
    }))

  const fromList = pickingList || Boolean(input.worldListId)
  const choice = worldChoice({ fromList, typedWorld, worldId: input.worldId })

  const pickRepeatEnd = (next: RepeatEnd) => {
    setRepeatEnd(next)
    setInput((current) => ({
      ...current,
      repeatUntil: next === 'date' ? current.repeatUntil : null,
      repeatTimes: next === 'times' ? (current.repeatTimes ?? FIRST_TIMES) : null,
    }))
  }

  // A saved template keeps the Google tick it has (null stays null), as a saved event does.
  const isNew = !event && !template?.id
  // VRChat's roles are not in the form: an event shown only to some roles keeps them, and stays
  // members-only for Google Calendar.
  const roleIds = event?.vrChatRoleIds ?? null
  const google = googleChip(input, roleIds, ready, isNew)

  const body = (draft: boolean): CalendarEventInput => ({
    ...input,
    // The form has no box for the event's own title and description: each place's words are typed on its
    // tab, and the server's (which it asks for) come from them.
    ...sharedNow(),
    // What the chip shows is what is saved, a new event's starting tick included.
    publishToGoogle: isNew ? googleTicked(input, roleIds, ready, true) : input.publishToGoogle,
    repeatUntil: repeatEnd === 'date' ? input.repeatUntil : null,
    // An emptied Times is sent as 0, so it is refused rather than saved as never ending.
    repeatTimes: repeatEnd === 'times' ? (input.repeatTimes ?? 0) : null,
    languages: splitList(languages),
    tags: splitList(tags),
    draft,
    // Off with the channel post it needs, rather than a refusal for a box that cannot be ticked.
    announceFirstJoinInDiscord: input.announceFirstJoinInDiscord && input.postToChannel && input.channelId !== null,
    // The reply goes under the date's Bluesky post, so it is off with Bluesky.
    announceFirstJoinOnBluesky: input.announceFirstJoinOnBluesky && !!input.publishToBluesky,
  })

  // The picked world's name and picture: the event as saved first (it knows a Seen world the form has not
  // loaded), then the lists' worlds and Seen's once loaded.
  const pickedWorld = [
    ...(event?.worldId ? [{ worldId: event.worldId, name: event.worldName, thumbnailUrl: event.worldThumbnailUrl }] : []),
    ...lists.flatMap((l) => l.worlds),
    ...(seenWorlds ?? []),
  ].find((w) => w.worldId === input.worldId)

  const missingList = listMissing({ choice, worldListId: input.worldListId ?? null, worldId: input.worldId })

  // The lines to show before a save: what the form leaves empty, a title that is not the template's,
  // and an event already at that time. The check for that one failing is no reason to hold the save.
  const warningsFor = async (saving: CalendarEventInput): Promise<string[]> => {
    // A slow answer is no answer: the save goes ahead without the duplicate lines.
    let timer = 0
    const none = new Promise<never[]>((resolve) => {
      timer = window.setTimeout(() => resolve([]), SAME_EVENTS_WAIT_MS)
    })
    const same = await Promise.race([
      calendarApi
        .sameEvents(saving.title, saving.startsAt, saving.timeZone, event?.id ?? null)
        .then((answer) => answer.matches)
        .catch(() => []),
      none,
    ])
    window.clearTimeout(timer)

    return saveWarnings(
      {
        title: saving.title,
        publishToVRChat: saving.publishToVRChat,
        languages: saving.languages,
        tags: saving.tags,
        templateTitle,
      },
      same,
      saving.timeZone,
    )
  }

  const save = async (draft: boolean) => {
    // A draft goes nowhere, so it needs no world. Scheduled, the list matters only to VRChat (its
    // calendar, or the instance Modbot opens), and its field is on VRChat's tab.
    if (!draft && (input.publishToVRChat || input.autoOpen) && missingList) {
      setListMissed(true)
      openTab('vrchat')
      return
    }

    setBusy(true)
    setError(null)

    let saving = body(draft)

    // A draft is not published, so it is not looked over. Warnings never refuse a save: the same
    // warnings shown once are not shown again, and the save goes through.
    if (!template && !draft) {
      const found = await warningsFor(saving)
      const going = DESTINATIONS.filter((place) => place !== 'feed' && on(place)).map((place) => DESTINATION_LABEL[place])
      if (turnedOn.current && going.length > 0) found.push(`Goes to ${going.join(', ')}`)

      if (found.length > 0 && found.join('\n') !== warnings.join('\n')) {
        setWarnings(found)
        setBusy(false)
        return
      }

      setWarnings(found)
    }

    // A crop still in a box goes up first; refused, nothing is saved and the field says why.
    if (vrchatPicture.draft && input.publishToVRChat) {
      const fileId = await vrchatPicture.upload()

      if (!fileId) {
        setBusy(false)
        openTab('vrchat')
        return
      }

      saving = { ...saving, vrChatImageId: fileId }
    }

    if (discordCover.draft && discordOn) {
      const coverId = await discordCover.upload()

      if (!coverId) {
        setBusy(false)
        openTab(input.publishToDiscord ? 'discordEvent' : 'channelPost')
        return
      }

      saving = { ...saving, coverPictureId: coverId }
    }

    if (template) {
      const fields = fieldsFromInput(saving, length)
      const name = templateName.trim()
      const request = template.id ? calendarTemplateApi.update(template.id, name, fields) : calendarTemplateApi.create(name, fields)

      request
        .then(() => onTemplateSaved?.())
        .catch((e: unknown) => setError(e instanceof ApiError ? problemsOf(e) : ['Could not save the template.']))
        .finally(() => setBusy(false))
      return
    }

    const request = event ? calendarApi.update(event.id, saving) : calendarApi.create(saving)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? problemsOf(e) : ['Could not save the event.']))
      .finally(() => setBusy(false))
  }

  const isDraft = !event || event.state === 'draft'

  // Anything typed or picked since the form opened, a crop not uploaded yet included.
  const unsaved =
    JSON.stringify(input) !== JSON.stringify(opened.input) ||
    languages !== opened.languages ||
    tags !== opened.tags ||
    templateName !== opened.templateName ||
    length !== opened.length ||
    repeatEnd !== opened.repeatEnd ||
    vrchatPicture.draft !== null ||
    discordCover.draft !== null ||
    uploading

  // Cancel, Escape, the X and a click outside all come here: closing with something unsaved asks first.
  const leave = () => (unsaved ? setAsking(true) : onClose())

  // Discord's picture is for the Discord event and the channel post.
  const discordOn = input.publishToDiscord || input.postToChannel

  // A picture chosen on this computer opens in every crop box shown, so one choice serves both
  // places; each is cropped to its own shape.
  const chooseEverywhere = (file: Blob) => {
    if (pictureUploads && vrchatPlus !== false && input.publishToVRChat) vrchatPicture.openFile(file)
    if (discordOn) discordCover.openFile(file)
  }

  // Bluesky's pictures: its own list once edited, else the cover. Every edit writes the whole list.
  const editBlueskyPictures = (change: (list: BlueskyPicture[]) => BlueskyPicture[]) =>
    setInput((current) => ({
      ...current,
      blueskyPictures: change(shownPictures(current.blueskyPictures, current.coverPictureId)),
      blueskyGif: hasGif(current.blueskyGif) ? NO_GIF : current.blueskyGif,
    }))

  // Each picture is kept by Modbot as a cover is, with the small JPEG copy Bluesky is sent.
  const addBlueskyPictures = async (files: File[]) => {
    const room = BLUESKY_PICTURES_MAX - shownPictures(input.blueskyPictures, input.coverPictureId).length
    setBlueskyMediaError(null)
    setBlueskyUploads((n) => n + 1)

    try {
      for (const file of files.slice(0, Math.max(room, 0))) {
        if (file.size > CALENDAR_COVER_MAX_BYTES) {
          setBlueskyMediaError('A picture is larger than 8 MB.')
          continue
        }

        const { coverId } = await calendarApi.uploadCover(file)

        try {
          await calendarApi.uploadCoverBlueskyCopy(coverId, await smallJpeg(file, BLUESKY_PICTURE_MAX_BYTES))
        } catch {
          // Nothing to say: the picture is kept, and goes as it is when it is under 1 MB.
        }

        editBlueskyPictures((list) => withPictures(list, [{ pictureId: coverId, alt: '' }]))
      }
    } catch (e) {
      setBlueskyMediaError(e instanceof ApiError ? problemsOf(e)[0] : 'Could not add the picture.')
    } finally {
      setBlueskyUploads((n) => n - 1)
    }
  }

  // Bluesky's video, instead of pictures: a file is kept by Modbot as it is picked, a link is only an address.
  const setBlueskyVideo = (video: BlueskyVideo) =>
    setInput((current) => ({
      ...current,
      blueskyVideo: video,
      blueskyPictures: hasVideo(video) ? [] : current.blueskyPictures,
      blueskyGif: hasVideo(video) && hasGif(current.blueskyGif) ? NO_GIF : current.blueskyGif,
    }))

  // Bluesky's GIF, instead of pictures or a video.
  const setBlueskyGif = (gif: BlueskyGif) =>
    setInput((current) => ({
      ...current,
      blueskyGif: gif,
      blueskyPictures: hasGif(gif) ? [] : current.blueskyPictures,
      blueskyVideo: hasGif(gif) ? NO_VIDEO : current.blueskyVideo,
    }))

  const addBlueskyVideoFile = async (file: File) => {
    const problem = videoFileProblem(file)
    setBlueskyMediaError(problem)
    if (problem) return

    setBlueskyVideoUploads((n) => n + 1)
    try {
      const { videoId } = await calendarApi.uploadBlueskyVideo(file)
      setBlueskyVideo(videoFromFile(videoId))
    } catch (e) {
      setBlueskyMediaError(e instanceof ApiError ? problemsOf(e)[0] : 'Could not add the video.')
    } finally {
      setBlueskyVideoUploads((n) => n - 1)
    }
  }

  const addBlueskyVideoLink = (url: string): boolean => {
    const problem = videoLinkProblem(url)
    setBlueskyMediaError(problem)
    if (problem) return false

    setBlueskyVideo(videoFromLink(url))
    return true
  }

  const discordPreview = discordCover.draft ? (
    <CroppedPicture
      picture={discordCover.draft.picture}
      box={discordCover.draft.box}
      aspect={DISCORD_COVER_ASPECT}
    />
  ) : null

  // What the VRChat card shows as VRChat's picture: a crop not uploaded yet, or the set picture when this
  // page can see it (one uploaded here, or the picture link's own).
  const vrchatAddress = vrchatPictureAddress(input.vrChatImageId, input.imageUrl, pictureThumbnails)
  const vrchatPreview = vrchatPicture.draft ? (
    <CroppedPicture
      picture={vrchatPicture.draft.picture}
      box={vrchatPicture.draft.box}
      aspect={VRCHAT_PICTURE_ASPECT}
      className="rounded-sm"
    />
  ) : vrchatAddress ? (
    <img src={vrchatAddress} alt="" className="aspect-video w-full rounded-sm object-cover" />
  ) : null
  const on = (place: CalendarDestination) =>
    place === 'feed' || (place === 'googleCalendar' ? google.on : !!input[DESTINATION_SWITCH[place]])

  const sharedNow = () => sharedWords(input, SERVICE_TABS.filter((place) => on(place)))

  const tabs = tabsFor((place) => on(place) || placeReady(place, ready))
  // A place goes off, or a Google tick goes off with its setting: the tab goes with it, so turning
  // it back on does not open the tab again.
  const active: CalendarTab = tabs.includes(tab) ? tab : 'event'
  if (tab !== active) setTab(active)
  // The Discord event and the channel post share one Discord tab; the part last clicked is the active place.
  const isDiscord = active === 'discordEvent' || active === 'channelPost'
  const discordTab = DISCORD_PARTS.find((place) => tabs.includes(place)) ?? null

  // Whether a place was turned on by changing its tab, so Schedule asks once.
  const turnedOn = useRef(false)
  const turnOn = (place: CalendarServiceTab | 'instance') => {
    const key = place === 'googleCalendar' ? 'publishToGoogle' : DESTINATION_SWITCH[place]
    turnedOn.current = true
    setInput((current) => (current[key] ? current : { ...current, [key]: true }))
  }

  // A change made on a service's tab turns that place on. The instance's fields, on the VRChat tab,
  // turn on opening the instance rather than the VRChat calendar.
  const byInstance = useRef(false)
  const setInstance: typeof set = (key, value) => {
    byInstance.current = true
    set(key, value)
    turnOn('instance')
  }
  // A change that turns its own place off (no channel for the channel post) must not turn it back on.
  const turningOff = useRef(false)
  const last = useRef({ input, languages, tags })
  useEffect(() => {
    const before = last.current
    last.current = { input, languages, tags }
    if (byInstance.current || turningOff.current) {
      byInstance.current = false
      turningOff.current = false
      return
    }
    if (active === 'event') return
    const changed =
      before.languages !== languages ||
      before.tags !== tags ||
      (Object.keys(input) as (keyof CalendarEventInput)[]).some(
        (key) => !PLACE_SWITCHES.has(key) && JSON.stringify(before.input[key]) !== JSON.stringify(input[key]),
      )
    if (changed) turnOn(active)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [input, languages, tags])

  // The form as the server is asked to draw it. A template has no dates: its end follows its length,
  // and a card is drawn for at least a minute.
  const previewInput: CalendarEventInput = template
    ? { ...body(false), endsAt: addMinutes(input.startsAt, Math.max(length, 1)) }
    : body(false)
  const previewKey = JSON.stringify(previewInput)
  const shownKey = useRef('')

  // A service's tab draws its card from the form as it is when the tab opens.
  const openTab = (next: CalendarTab) => {
    if (next !== 'event' && previewKey !== shownKey.current) {
      shownKey.current = previewKey
      setShown(previewInput)
    }

    setTab(next)
  }

  // Fields sit beside the card they change, so the card follows them once typing stops.
  useEffect(() => {
    if (active === 'event' || previewKey === shownKey.current) return

    const timer = window.setTimeout(() => {
      shownKey.current = previewKey
      setShown(JSON.parse(previewKey) as CalendarEventInput)
    }, PREVIEW_WAIT_MS)

    return () => window.clearTimeout(timer)
  }, [active, previewKey])

  const answer = usePreviewAnswer(event?.id ?? null, shown)

  // A service's title and description on its card, as boxes to type in. They hold the service's own words,
  // or the event's while it has none, and the first typing makes them the service's own. The card draws
  // them from the form itself, so typing never waits for the server's drawing.
  const editingFor = (place: CalendarServiceTab, asked: CalendarEventInput): CardEditing => {
    const keys = OWN_TEXT[place]

    const field = (key: OwnKey, isTitle: boolean): CardField => {
      // No shared title or description box: each place holds only its own words.
      const value = input[key] ?? ''

      return {
        value,
        onChange: (next) => {
          setEdited((current) => new Set(current).add(key))
          set(key, next)
        },
        onActive: (insert) => {
          setPutFillIn({ insert })
          if (!isTitle) setPutLink({ insert })
        },
        // Empty, the place is sent the event's words: they show muted in the box.
        placeholder: placeholderOf({ ...input, ...sharedNow() }, key, EVERY_OWN_KEY),
        count: countOf(key, value, true),
        asked: textShown(asked, key, NO_OWN_TEXT),
      }
    }

    return {
      title: field(keys.title, true),
      description: field(keys.description, false),
      own: edited.has(keys.title) || edited.has(keys.description),
      useEventText: () => {
        setEdited((current) => {
          const next = new Set(current)
          next.delete(keys.title)
          next.delete(keys.description)
          return next
        })
        setInput((current) => ({ ...current, [keys.title]: '', [keys.description]: '' }))
      },
    }
  }

  const card = (place: CalendarServiceTab) =>
    shown && (
      <EventPreview
        answer={answer}
        places={[place]}
        vrchatPicture={vrchatPreview}
        discordPicture={discordPreview}
        editing={editingFor(place, shown)}
      />
    )

  // Discord's picture is cropped on the Discord event's tab, or on the post's when there is no event.
  const coverField = (
    <DiscordPictureField
      picture={discordCover}
      value={input.coverPictureId}
      link={input.imageUrl}
      onChange={(id) => set('coverPictureId', id)}
      onFile={chooseEverywhere}
    />
  )

  // The picture link only feeds Discord, so it sits on the Discord tabs (same value on both).
  const pictureLinkField = (
    <Field
      label="Picture link"
      value={input.imageUrl ?? ''}
      placeholder="https://"
      onChange={(v) =>
        // A link from VRChat fills the VRChat picture too (§15.1).
        setInput((current) => {
          const imageUrl = v.trim() || null
          return {
            ...current,
            imageUrl,
            vrChatImageId: pictureFollowingLink(current.imageUrl, imageUrl, current.vrChatImageId),
          }
        })
      }
    />
  )


  const fillIns = useMemo(() => {
    if (!template) return []
    const inTexts = fillInsIn(FILL_IN_TEXTS.map((field) => input[field]))
    const known = new Set(inTexts.map(fillInKey))
    return [...inTexts, ...spareFillIns.filter((name) => !known.has(fillInKey(name)))]
  }, [template, input, spareFillIns])

  // A fill-in or a link goes where the caret last was; with no text clicked into yet, on the end of the title.
  // The field last clicked into may have gone (another tab is open): then the open tab's own title,
  // which on a service's tab is that service's.
  const putIn = (name: string) => {
    const keys = active === 'event' ? { title: 'title' as const, description: 'description' as const } : OWN_TEXT[active]

    const append = (key: 'title' | 'description' | OwnKey) => {
      if (key !== 'title' && key !== 'description') setEdited((current) => new Set(current).add(key))
      set(key, (key === 'title' || key === 'description' ? input[key] : textShown(input, key, edited)) + fillInMarker(name))
    }

    if (autoLinkOf(name)) {
      // Links go in descriptions only: the one last clicked into, else the end of the open tab's.
      if (!putLink?.insert(name)) append(keys.description)
      return
    }

    if (!putFillIn?.insert(name)) append(keys.title)
  }

  const addFillIn = (raw: string) => {
    const name = cleanFillInName(raw)
    if (name === '') return

    // A name that means an automatic link is that link, not a blank to ask for.
    const link = autoLinkOf(name)
    if (link) {
      setNewFillIn('')
      putIn(link)
      return
    }

    const taken = fillIns.find((n) => fillInKey(n) === fillInKey(name))
    const shown = taken ?? name
    if (!taken) setSpareFillIns((current) => [...current, shown])
    setNewFillIn('')
    putIn(shown)
  }

  // The fill-ins of a template and the automatic links, to put at the caret of the text last clicked into:
  // above the event's own texts, and above a service's card.
  const insertRows = (
    <>
      {template && (
        <Section title="Fill-ins">
          <div className="flex flex-wrap items-center gap-2">
            {fillIns.map((name) => (
              <Button key={fillInKey(name)} size="sm" variant="outline" onClick={() => addFillIn(name)}>
                {name}
              </Button>
            ))}
            <Input
              className="w-44"
              aria-label="New fill-in"
              value={newFillIn}
              onChange={(e) => setNewFillIn(e.target.value)}
              onKeyDown={(e) => {
                if (e.key !== 'Enter') return
                e.preventDefault()
                addFillIn(newFillIn)
              }}
            />
            <Button size="sm" variant="outline" disabled={cleanFillInName(newFillIn) === ''} onClick={() => addFillIn(newFillIn)}>
              Add
            </Button>
          </div>
        </Section>
      )}
    </>
  )

  return (
    <Dialog open onOpenChange={(open) => !open && leave()}>
      <DialogContent
        title={template ? (template.id ? 'Edit template' : 'New template') : event ? 'Edit event' : 'New event'}
        // Always as tall as a dialog may be, so switching tabs never changes its height; the body scrolls.
        className="h-[calc(100dvh-1.5rem)] max-w-[720px] sheet:h-[calc(var(--visible-h,100dvh)*0.85)]"
        foot={
          <DialogFoot>
            {error && (
              <span role="alert" className="mr-auto flex min-w-0 basis-full flex-col [overflow-wrap:anywhere] sm:basis-auto">
                {error.map((line) => (
                  <Outcome key={line} tone="problem">
                    {line}
                  </Outcome>
                ))}
              </span>
            )}
            {!error && warnings.length > 0 && (
              <span role="status" className="mr-auto flex min-w-0 basis-full flex-col text-warn [overflow-wrap:anywhere] sm:basis-auto" style={{ fontSize: 'var(--text-small)' }}>
                {warnings.map((line) => (
                  <span key={line}>{line}</span>
                ))}
              </span>
            )}
            <Button size="sm" variant="outline" disabled={busy} onClick={leave}>
              Cancel
            </Button>
            {isDraft && !template && (
              <Button size="sm" variant="outline" disabled={busy || uploading} onClick={() => void save(true)}>
                Save draft
              </Button>
            )}
            <Button size="sm" disabled={busy || uploading} onClick={() => void save(false)}>
              {isDraft && !template ? 'Schedule' : 'Save'}
              {warnings.length > 0 && ' anyway'}
            </Button>
          </DialogFoot>
        }
      >
        <Tabs
          value={discordTab && isDiscord ? discordTab : active}
          onChange={openTab}
          tabs={tabs
            .filter((value) => !(value === 'channelPost' && tabs.includes('discordEvent')))
            .map((value) => ({
              value,
              label: value === discordTab ? 'Discord' : TAB_LABEL[value],
              mark: value !== 'event' && (on(value) || (value === 'vrchat' && input.autoOpen) || (value === discordTab && DISCORD_PARTS.some(on))),
            }))}
          end={<AddPlace ready={ready} />}
          panelClassName="overflow-visible pt-4"
        >
          {active === 'event' ? (
            <div className="flex min-w-0 flex-col gap-4">
              {template && <Field label="Name" value={templateName} onChange={setTemplateName} />}
              {insertRows}

              {categoryChoices.length > 0 && (
                <Section title="Categories">
                  <div role="group" aria-label="Categories" className="flex flex-wrap gap-2">
                    {categoryChoices.map((c) => (
                      <Chip key={c.id} on={input.categoryIds.includes(c.id)} onClick={() => toggle('categoryIds', c.id)}>
                        {input.categoryIds.includes(c.id) && <Check className="size-3.5" />}
                        {c.name}
                      </Chip>
                    ))}
                  </div>
                </Section>
              )}

              <Section title="When">
                <div className="grid gap-3 sm:grid-cols-2">
                  {template ? (
                    <Labelled label="Length">
                      <span className="flex items-center gap-2">
                        <Input
                          type="number"
                          min={0}
                          max={168}
                          className="w-20"
                          aria-label="Hours"
                          value={splitLength(length).hours}
                          onChange={(e) => setLength(joinLength(Number(e.target.value), splitLength(length).minutes))}
                        />
                        <span>h</span>
                        <Input
                          type="number"
                          min={0}
                          max={59}
                          className="w-20"
                          aria-label="Minutes"
                          value={splitLength(length).minutes}
                          onChange={(e) => setLength(joinLength(splitLength(length).hours, Number(e.target.value)))}
                        />
                        <span>min</span>
                      </span>
                    </Labelled>
                  ) : (
                    <>
                      <Labelled label="Starts">
                        <Input type="datetime-local" value={input.startsAt} onChange={(e) => set('startsAt', e.target.value)} />
                      </Labelled>
                      <Labelled label="Ends">
                        <Input type="datetime-local" value={input.endsAt} onChange={(e) => set('endsAt', e.target.value)} />
                      </Labelled>
                      <Labelled label="Time zone">
                        <Select value={input.timeZone} onChange={(v) => set('timeZone', v)} aria-label="Time zone">
                          {!zones.includes(input.timeZone) && <option value={input.timeZone}>{input.timeZone}</option>}
                          {zones.map((z) => (
                            <option key={z} value={z}>
                              {z}
                            </option>
                          ))}
                        </Select>
                      </Labelled>
                    </>
                  )}
                  <Labelled label="Repeat">
                    <Select value={input.repeat} onChange={(v) => set('repeat', v as CalendarRepeat)} aria-label="Repeat">
                      <option value="none">Does not repeat</option>
                      <option value="daily">Daily</option>
                      <option value="weekly">Weekly</option>
                      <option value="monthly">Monthly</option>
                    </Select>
                  </Labelled>
                </div>

                {input.repeat === 'weekly' && (
                  <div className="flex flex-wrap gap-3">
                    {DAYS.map((d) => (
                      <Checkbox key={d.value} checked={input.repeatDays.includes(d.value)} onChange={() => toggle('repeatDays', d.value)}>
                        {d.label}
                      </Checkbox>
                    ))}
                  </div>
                )}

                {input.repeat !== 'none' && (
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Labelled label="Every">
                      <span className="flex items-center gap-2">
                        <Input
                          type="number"
                          min={1}
                          max={52}
                          className="w-20"
                          value={input.repeatEvery}
                          onChange={(e) => set('repeatEvery', Number(e.target.value))}
                        />
                        <span>{REPEAT_UNIT[input.repeat][input.repeatEvery === 1 ? 0 : 1]}</span>
                      </span>
                    </Labelled>
                    <Labelled label="Ends">
                      <Select value={repeatEnd} onChange={(v) => pickRepeatEnd(v as RepeatEnd)} aria-label="Ends">
                        <option value="never">Never</option>
                        {!template && <option value="date">On a date</option>}
                        <option value="times">After a number of times</option>
                      </Select>
                    </Labelled>
                    {repeatEnd === 'date' && (
                      <Labelled label="Last date">
                        <Input
                          type="date"
                          value={input.repeatUntil ?? ''}
                          onChange={(e) => set('repeatUntil', e.target.value || null)}
                        />
                      </Labelled>
                    )}
                    {repeatEnd === 'times' && (
                      <Labelled label="Times">
                        <Input
                          type="number"
                          min={1}
                          max={500}
                          className="w-28"
                          value={input.repeatTimes ?? ''}
                          onChange={(e) => set('repeatTimes', e.target.value === '' ? null : Number(e.target.value))}
                        />
                      </Labelled>
                    )}
                  </div>
                )}
              </Section>

              <Section title="Where">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Labelled label="Platform">
                    <Select value="vrchat" onChange={() => {}} aria-label="Platform">
                      <option value="vrchat">VRChat</option>
                    </Select>
                  </Labelled>
                  <Labelled label="Who can join">
                    <Select value={input.accessType} onChange={(v) => set('accessType', v)} aria-label="Who can join">
                      <option value="members">Group members</option>
                      <option value="plus">Members and their friends</option>
                      <option value="public">Anyone</option>
                    </Select>
                  </Labelled>
                  <Labelled label="Visible to">
                    <Select value={input.visibility} onChange={(v) => set('visibility', v)} aria-label="Visible to">
                      <option value="group">Group</option>
                      <option value="public">Everyone</option>
                    </Select>
                  </Labelled>
                </div>
              </Section>

            </div>
          ) : (
            <div className="flex min-w-0 flex-col gap-4">
              {active === 'vrchat' && (
                <VRChatPreviews
                  title={input.vrChatTitle || sharedNow().title}
                  description={input.vrChatDescription || sharedNow().description}
                  picture={vrchatAddress ?? pickedWorld?.thumbnailUrl ?? null}
                  platforms={input.platforms}
                  aboutCount={editingFor('vrchat', shown ?? input).description.count}
                  titleBox={(() => {
                    const f = editingFor('vrchat', shown ?? input).title
                    return (
                      <span className="flex items-center gap-1">
                        <FillInText
                          label="Title"
                          links={false}
                          value={f.value}
                          onChange={f.onChange}
                          onActive={f.onActive}
                          placeholder={f.placeholder || 'Add a title'}
                          className="h-auto min-w-0 flex-1 border-none bg-transparent p-0 text-inherit text-[length:inherit]! font-bold focus-visible:ring-0 empty:before:font-normal empty:before:text-current empty:before:opacity-50"
                        />
                        <CopyButton text={f.value} />
                        <PasteButton onPaste={(t) => f.onChange(f.value + t)} />
                      </span>
                    )
                  })()}
                  about={(() => {
                    const f = editingFor('vrchat', shown ?? input).description
                    return (
                      <span className="flex items-start gap-1">
                        <FillInText
                          multiline
                          label="About this event"
                          value={f.value}
                          onChange={f.onChange}
                          onActive={f.onActive}
                          placeholder={f.placeholder || 'Add a description'}
                          className="min-h-0 min-w-0 flex-1 border-none bg-transparent p-0 font-semibold text-inherit focus-visible:ring-0 empty:before:font-normal empty:before:text-current empty:before:opacity-50"
                        />
                        <LinkMenu onPick={putIn} />
                        <CopyButton text={f.value} />
                        <PasteButton onPaste={(t) => f.onChange(f.value + t)} />
                      </span>
                    )
                  })()}
                  repeats={input.repeat !== 'none'}
                  languages={splitList(languages)}
                  onLanguages={(list) => setLanguages(list.join(', '))}
                  eventTags={splitList(tags)}
                  onEventTags={(list) => setTags(list.join(', '))}
                  category={input.category}
                  categoryOptions={Object.fromEntries(categories.map((c) => [c, CATEGORY_LABEL[c] ?? c]))}
                  onCategory={(v) => set('category', v)}
                  featured={input.featured}
                  startsAt={input.startsAt}
                  endsAt={input.endsAt}
                  publicEvent={input.visibility === 'public'}
                />
              )}
              {isDiscord &&
                DISCORD_PARTS.filter((place) => tabs.includes(place)).map((place) => (
                  <div
                    key={place}
                    className="flex min-w-0 flex-col gap-4"
                    onPointerDownCapture={() => active !== place && setTab(place)}
                    onFocusCapture={() => active !== place && setTab(place)}
                  >
                    <h3 className="font-label text-muted-foreground">{DESTINATION_LABEL[place]}</h3>
                    {on(place) && !isSetUp(place, ready) && <NotSetUp place={place} newTab />}
                    {(() => {
                const e = editingFor(place, shown ?? input)
                const drawn = answer.preview
                return (
                  <DiscordPreviews
                    key={place}
                    place={place}
                    title={e.title.value || sharedNow().title}
                    description={e.description.value || sharedNow().description}
                    startsAt={input.startsAt}
                    endsAt={input.endsAt}
                    location={drawn?.discordEvent?.location ?? null}
                    picture={discordPreview}
                    pictureUrl={(place === 'discordEvent' ? drawn?.discordEvent?.coverUrl : drawn?.channelPost?.pictureUrl) ?? input.imageUrl ?? null}
                    onPicture={chooseEverywhere}
                    groupName={drawn?.channelPost?.groupName ?? null}
                    post={drawn?.channelPost ?? null}
                    colour={input.postColour}
                    onColour={(c) => set('postColour', c)}
                    aboutCount={e.description.count}
                    titleBox={
                      <span className="flex items-center gap-1">
                        <FillInText
                          label="Title"
                          links={false}
                          value={e.title.value}
                          onChange={e.title.onChange}
                          onActive={e.title.onActive}
                          placeholder={e.title.placeholder || 'Add a title'}
                          className="h-auto min-w-0 flex-1 border-none bg-transparent p-0 text-inherit text-[length:inherit]! font-[inherit] focus-visible:ring-0 empty:before:font-normal empty:before:text-current empty:before:opacity-50"
                        />
                        <CopyButton text={e.title.value} />
                        <PasteButton onPaste={(t) => e.title.onChange(e.title.value + t)} />
                      </span>
                    }
                    about={
                      <span className="flex items-start gap-1">
                        <FillInText
                          multiline
                          label="Description"
                          value={e.description.value}
                          onChange={e.description.onChange}
                          onActive={e.description.onActive}
                          placeholder={e.description.placeholder || 'Add a description'}
                          className="min-h-0 min-w-0 flex-1 border-none bg-transparent p-0 text-inherit text-[length:inherit]! focus-visible:ring-0 empty:before:text-current empty:before:opacity-50"
                        />
                        <LinkMenu onPick={putIn} />
                        <CopyButton text={e.description.value} />
                        <PasteButton onPaste={(t) => e.description.onChange(e.description.value + t)} />
                      </span>
                    }
                  />
                )
                    })()}
              {place === 'discordEvent' && (
                <Section>
                  {pictureLinkField}
                  {coverField}
                </Section>
              )}
              {place === 'channelPost' && (
                <Section>
                  {pictureLinkField}
                  {!input.publishToDiscord && coverField}
                  <ChannelPicker
                    label="Channel"
                    value={input.channelId ?? ''}
                    // No channel means no channel post: the server refuses a post with nowhere to go.
                    onChange={(id) => {
                      // Only when something changes, or the flag would swallow the next real change.
                      if (!id && (input.channelId || input.postToChannel)) turningOff.current = true
                      setInput((current) => ({ ...current, channelId: id || null, postToChannel: id ? current.postToChannel : false }))
                    }}
                    needs={['viewChannel', 'sendMessages', 'embedLinks']}
                    allowNone
                  />
                  <RolePicker
                    label="Mention role"
                    value={input.mentionRoleId ?? ''}
                    onChange={(id) => set('mentionRoleId', id || null)}
                    needsMention
                  />
                </Section>
              )}
                  </div>
                ))}
              {active === 'googleCalendar' ? (
                google.note === 'notSetUp' ? (
                  <NotSetUp place="googleCalendar" newTab />
                ) : (
                  google.note === 'membersOnly' && (
                    <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                      Members only
                    </span>
                  )
                )
              ) : (
                !isDiscord && on(active) && !isSetUp(active, ready) && <NotSetUp place={active} newTab />
              )}
              {!isDiscord && active !== 'bluesky' && card(active)}
              {active === 'bluesky' && answer.error && <Outcome tone="problem">{answer.error}</Outcome>}
              {active === 'bluesky' &&
                (() => {
                  const e = editingFor('bluesky', shown ?? input)
                  const drawn = answer.preview?.bluesky ?? null
                  const lines = drawn ? postAround(drawn.text, e.title.asked, e.description.asked) : { before: '', after: '' }
                  const video = input.blueskyVideo
                  const gif = input.blueskyGif
                  const pictures = hasVideo(video) || hasGif(gif) ? [] : shownPictures(input.blueskyPictures, input.coverPictureId)
                  return (
                    <BlueskyPreviews
                      title={e.title.value || sharedNow().title}
                      before={lines.before}
                      after={lines.after}
                      pictures={pictures.map((picture) => ({ id: picture.pictureId, url: coverAddress(picture.pictureId), alt: picture.alt }))}
                      onAddFiles={(files) => void addBlueskyPictures(files)}
                      onAlt={(index, alt) => editBlueskyPictures((list) => withPictureChanged(list, index, { alt }))}
                      onRemove={(index) => editBlueskyPictures((list) => withoutPicture(list, index))}
                      uploading={blueskyUploads > 0}
                      video={
                        video && hasVideo(video)
                          ? { url: video.videoId ? blueskyVideoAddress(video.videoId) : null, link: video.url ?? null, alt: video.alt ?? '' }
                          : null
                      }
                      onVideoFile={(file) => void addBlueskyVideoFile(file)}
                      onVideoLink={addBlueskyVideoLink}
                      onVideoAlt={(alt) => set('blueskyVideo', video ? videoWithAlt(video, alt) : video)}
                      onVideoRemove={() => set('blueskyVideo', NO_VIDEO)}
                      videoUploading={blueskyVideoUploads > 0}
                      gif={gif && hasGif(gif) ? { url: gif.url ?? '', alt: gif.alt ?? '' } : null}
                      gifKey={gifSearchCode}
                      onGif={(picked) => setBlueskyGif(gifFromKlipy(picked))}
                      onGifAlt={(alt) => set('blueskyGif', gif ? gifWithAlt(gif, alt) : gif)}
                      onGifRemove={() => set('blueskyGif', NO_GIF)}
                      error={blueskyMediaError}
                      onEmoji={(emoji) => putBlueskyText?.put(emoji)}
                      whoCanReply={input.blueskyWhoCanReply ?? ['anyone']}
                      onWhoCanReply={(choice) => set('blueskyWhoCanReply', choice)}
                      quotesOff={input.blueskyQuotesOff ?? false}
                      onQuotesOff={(off) => set('blueskyQuotesOff', off)}
                      languages={input.blueskyLanguages ?? []}
                      onLanguages={(codes) => set('blueskyLanguages', codes)}
                      card={drawn?.card ?? null}
                      count={
                        drawn
                          ? { label: `${drawn.graphemes} / ${drawn.limit}`, over: drawn.graphemes > drawn.limit || drawn.bytes > BLUESKY_BYTE_LIMIT }
                          : e.description.count
                      }
                      titleBox={
                        <span className="flex items-center gap-1">
                          <FillInText
                            label="Title"
                            links={false}
                            value={e.title.value}
                            onChange={e.title.onChange}
                            onActive={e.title.onActive}
                            onPutText={(put) => setPutBlueskyText({ put })}
                            placeholder={e.title.placeholder || 'Add a title'}
                            className="h-auto min-w-0 flex-1 border-none bg-transparent p-0 text-inherit text-[length:inherit]! font-[inherit] focus-visible:ring-0 empty:before:font-normal empty:before:text-current empty:before:opacity-50"
                          />
                          <CopyButton text={e.title.value} />
                          <PasteButton onPaste={(t) => e.title.onChange(e.title.value + t)} />
                        </span>
                      }
                      about={
                        <span className="flex items-start gap-1">
                          <FillInText
                            multiline
                            label="Description"
                            value={e.description.value}
                            onChange={e.description.onChange}
                            onActive={e.description.onActive}
                            onPutText={(put) => setPutBlueskyText({ put })}
                            putFirst
                            placeholder={e.description.placeholder || 'Add a description'}
                            className="min-h-0 min-w-0 flex-1 border-none bg-transparent p-0 text-inherit text-[length:inherit]! focus-visible:ring-0 empty:before:text-current empty:before:opacity-50"
                          />
                          <LinkMenu onPick={putIn} />
                          <CopyButton text={e.description.value} />
                          <PasteButton onPaste={(t) => e.description.onChange(e.description.value + t)} />
                        </span>
                      }
                    />
                  )
                })()}
              {insertRows}
              {active === 'vrchat' && (
                <Section>
                  <div className="grid gap-3 sm:grid-cols-2">
                  <Labelled label="World">
                    <Select
                      aria-label="World"
                      value={choice}
                      onChange={(v) => {
                        if (v === FROM_LIST) {
                          setTypedWorld(false)
                          setPickingList(true)
                          // A world typed or picked before is not this event's world any more.
                          set('worldId', null)
                          return
                        }

                        setPickingList(false)
                        set('worldListId', null)

                        if (v === OTHER_WORLD) {
                          setTypedWorld(true)
                          return
                        }

                        setTypedWorld(false)
                        set('worldId', null)
                      }}
                    >
                      <option value={NO_WORLD}>None</option>
                      <option value={OTHER_WORLD}>World id</option>
                      <option value={FROM_LIST}>Pick from a list</option>
                    </Select>
                  </Labelled>
                  {choice === FROM_LIST && (
                    <Labelled label="World list">
                      <WorldListMenu
                        lists={lists}
                        seenWorlds={seenWorlds}
                        pickedWorld={pickedWorld}
                        onSeenOpen={loadSeenWorlds}
                        listId={input.worldListId ?? null}
                        onList={(id) => setInput((current) => ({ ...current, worldListId: id, worldId: null }))}
                        onWorld={(id) =>
                          setInput((current) => ({ ...current, worldListId: null, worldId: id }))
                        }
                      />
                      {listMissed && missingList && (
                        <span role="alert" className="text-destructive">
                          Pick a list
                        </span>
                      )}
                    </Labelled>
                  )}
                  {choice === OTHER_WORLD && (
                    <div className="flex min-w-0 flex-col gap-1.5">
                      <Field label="World id" value={input.worldId ?? ''} placeholder="wrld_…" onChange={(v) => set('worldId', v.trim() || null)} />
                      {(pickedWorld?.name || pickedWorld?.thumbnailUrl) && (
                        <div className="flex min-w-0 items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
                          {pickedWorld.thumbnailUrl && (
                            <img src={pickedWorld.thumbnailUrl} alt="" className="h-8 w-12 shrink-0 rounded-sm object-cover" />
                          )}
                          {pickedWorld.name && (
                            <span className="min-w-0 truncate" title={pickedWorld.name}>
                              {pickedWorld.name}
                            </span>
                          )}
                        </div>
                      )}
                    </div>
                  )}
                    <Labelled label="Region">
                      <Select value={input.region} onChange={(v) => set('region', v)} aria-label="Region">
                        <option value="us">US West</option>
                        <option value="use">US East</option>
                        <option value="eu">Europe</option>
                        <option value="jp">Japan</option>
                      </Select>
                    </Labelled>
                    <VRChatPictureField
                      uploads={pictureUploads}
                      plus={vrchatPlus}
                      picture={vrchatPicture}
                      thumbnails={pictureThumbnails}
                      value={input.vrChatImageId}
                      saved={event?.vrChatImageId ?? null}
                      link={input.imageUrl}
                      onChange={(id) => set('vrChatImageId', id)}
                      onFile={chooseEverywhere}
                    />
                  </div>
                  <div className="flex flex-wrap gap-3">
                    {platforms.map((p) => (
                      <Checkbox key={p} checked={input.platforms.includes(p)} onChange={() => toggle('platforms', p)}>
                        {PLATFORM_LABEL[p] ?? p}
                      </Checkbox>
                    ))}
                    <PlatformIcons platforms={input.platforms} />
                  </div>
                  <div className="flex flex-wrap gap-3">
                    <Checkbox checked={input.notifyMembers} onChange={(v) => set('notifyMembers', v)}>
                      Notify group members
                    </Checkbox>
                    <Checkbox checked={input.featured} onChange={(v) => set('featured', v)}>
                      Featured
                    </Checkbox>
                  </div>
                </Section>
              )}
              {active === 'vrchat' && (
                <Section title={DESTINATION_LABEL.instance}>
                  {input.autoOpen && !isSetUp('instance', ready) && <NotSetUp place="instance" newTab />}
                  <Labelled label="Minutes early">
                    <Input
                      type="number"
                      min={0}
                      max={120}
                      className="w-28"
                      value={input.openMinutesBefore}
                      onChange={(e) => setInstance('openMinutesBefore', Number(e.target.value))}
                    />
                  </Labelled>
                  <InviteFields input={input} choices={choices} listName={event?.inviteListName ?? null} set={setInstance} />
                </Section>
              )}
              {active === 'bluesky' && (
                <Section>
                  <Labelled label="Post it">
                    <Select
                      value={String(input.blueskyPostBeforeMinutes ?? DEFAULT_BLUESKY_BEFORE)}
                      onChange={(v) => set('blueskyPostBeforeMinutes', Number(v))}
                      aria-label="Post it"
                    >
                      {BLUESKY_BEFORE_CHOICES.map((c) => (
                        <option key={c.minutes} value={c.minutes}>
                          {c.label}
                        </option>
                      ))}
                    </Select>
                  </Labelled>
                  {!discordOn && (
                    <>
                      {pictureLinkField}
                      <DiscordPictureField
                        label="Picture"
                        picture={discordCover}
                        value={input.coverPictureId}
                        link={input.imageUrl}
                        onChange={(id) => set('coverPictureId', id)}
                        onFile={chooseEverywhere}
                      />
                    </>
                  )}
                  <Checkbox
                    // Ticked here, the change turns Bluesky on, so it is never greyed out on this tab.
                    checked={input.announceFirstJoinOnBluesky && !!input.publishToBluesky}
                    onChange={(v) => set('announceFirstJoinOnBluesky', v)}
                  >
                    Reply when the first person joins
                  </Checkbox>
                </Section>
              )}
            </div>
          )}
        </Tabs>
      </DialogContent>
      {asking && <DiscardChanges onKeep={() => setAsking(false)} onDiscard={onClose} />}
    </Dialog>
  )
}

/**
 * Who the event invites when its instance opens, and the post when the first person is in
 * (calendar auto-invite design §10). Shown only with "Open the instance automatically".
 */
function InviteFields({
  input,
  choices,
  listName,
  set,
}: {
  input: CalendarEventInput
  choices: CalendarInviteChoices
  /** The list already on the event, named even for somebody who may not pick lists. */
  listName: string | null
  set: <K extends keyof CalendarEventInput>(key: K, value: CalendarEventInput[K]) => void
}) {
  const toggleStaff = (id: string) =>
    set(
      'inviteStaffUserIds',
      input.inviteStaffUserIds.includes(id)
        ? input.inviteStaffUserIds.filter((s) => s !== id)
        : [...input.inviteStaffUserIds, id],
    )

  const canAnnounce = input.postToChannel && input.channelId !== null
  const lists = choices.lists

  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <Labelled label="Host">
          <Select value={input.inviteHostUserId ?? ''} onChange={(v) => set('inviteHostUserId', v || null)} aria-label="Host">
            <option value="">None</option>
            {choices.staff.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </Select>
        </Labelled>
        <Labelled label="Invite list">
          <Select
            value={input.inviteListId ?? ''}
            onChange={(v) => set('inviteListId', v || null)}
            aria-label="Invite list"
            disabled={lists === null}
          >
            <option value="">None</option>
            {lists === null
              ? input.inviteListId && <option value={input.inviteListId}>{listName ?? input.inviteListId}</option>
              : lists.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name}
                  </option>
                ))}
          </Select>
        </Labelled>
      </div>

      {choices.staff.length > 0 && (
        <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Staff</span>
          <div className="flex flex-wrap gap-3">
            {choices.staff.map((s) => (
              <Checkbox key={s.id} checked={input.inviteStaffUserIds.includes(s.id)} onChange={() => toggleStaff(s.id)}>
                {s.name}
              </Checkbox>
            ))}
          </div>
        </div>
      )}

      <div className="flex flex-wrap gap-3">
        <Checkbox
          checked={input.announceFirstJoinInDiscord && canAnnounce}
          disabled={!canAnnounce}
          onChange={(v) => set('announceFirstJoinInDiscord', v)}
        >
          Announce in Discord when the first person joins
        </Checkbox>
        <Checkbox checked={input.announceFirstJoinInVRChat} onChange={(v) => set('announceFirstJoinInVRChat', v)}>
          Announce in VRChat when the first person joins
        </Checkbox>
        <Checkbox
          checked={input.announceFirstJoinOnBluesky && !!input.publishToBluesky}
          disabled={!input.publishToBluesky}
          onChange={(v) => set('announceFirstJoinOnBluesky', v)}
        >
          Announce on Bluesky when the first person joins
        </Checkbox>
      </div>
    </>
  )
}

function Section({ title, children }: { title?: string; children: React.ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-3 border-t border-t-(length:--hairline) pt-3">
      {title && <legend className="pr-2 font-label">{title}</legend>}
      {children}
    </fieldset>
  )
}

function Labelled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex min-w-0 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

/**
 * Every problem a refused save names, in the server's order (calendar design §17.2): its
 * `problems`, or the one sentence an older answer carries.
 */
function problemsOf(e: ApiError): string[] {
  const detail = e.detail as { problems?: unknown } | null
  const problems = Array.isArray(detail?.problems) ? detail.problems.filter((p): p is string => typeof p === 'string') : []
  return problems.length > 0 ? problems : [e.message]
}

function splitList(text: string): string[] {
  return text
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
}

/** Every place off: where a new event made from nothing starts. */
const NO_PLACES = {
  publishToVRChat: false,
  publishToDiscord: false,
  postToChannel: false,
  autoOpen: false,
  publishToBluesky: false,
  publishToGoogle: false,
} satisfies Partial<CalendarEventInput>

/** The input's on and off switches for places, which a change on a tab sets rather than counts as. */
const PLACE_SWITCHES: ReadonlySet<keyof CalendarEventInput> = new Set(Object.keys(NO_PLACES) as (keyof CalendarEventInput)[])

/** Whether a place's tab is shown for an event not using it: the place is set up. An older server says nothing, and every tab shows. */
function placeReady(place: CalendarServiceTab, ready: CalendarReady | null | undefined): boolean {
  if (!ready) return true
  if (place === 'vrchat') return ready.vrChat
  if (place === 'googleCalendar') return ready.google !== false
  if (place === 'bluesky') return ready.bluesky !== false
  return ready.discord
}

/** The integrations not set up yet, each linking to where it is set up. */
const ADDABLE: { label: string; link: string; missing: (ready: CalendarReady) => boolean }[] = [
  { label: 'VRChat', link: SET_UP_LINK.vrchat, missing: (r) => !r.vrChat },
  { label: 'Discord', link: SET_UP_LINK.discordEvent, missing: (r) => !r.discord },
  { label: 'Google Calendar', link: SET_UP_LINK.googleCalendar, missing: (r) => r.google === false },
  { label: 'Bluesky', link: SET_UP_LINK.bluesky, missing: (r) => r.bluesky === false },
]

/** The + after the tabs: the integrations not set up, opened in a new tab so the form stays as typed. */
function AddPlace({ ready }: { ready: CalendarReady | null | undefined }) {
  const [open, setOpen] = useState(false)
  const mayFollow = useCanManageSettings()
  const missing = ready ? ADDABLE.filter((a) => a.missing(ready)) : []

  if (!mayFollow || missing.length === 0) return null

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button
          type="button"
          aria-label="Add a place"
          className="flex shrink-0 items-center px-2 text-muted-foreground transition-colors hover:text-foreground sm:px-3"
          style={{ minHeight: 'var(--control-h)' }}
        >
          <Plus className="size-4" />
        </button>
      </Popover.Trigger>
      <PopoverPortal>
        <Popover.Content
          align="start"
          sideOffset={4}
          className="z-50 flex w-44 flex-col rounded-sm border border-(length:--hairline) bg-popover p-1 text-popover-foreground shadow-sm"
          style={{ fontSize: 'var(--text-small)' }}
        >
          {missing.map((a) => (
            <a
              key={a.label}
              href={a.link}
              target="_blank"
              rel="noreferrer"
              onClick={() => setOpen(false)}
              className="rounded-sm px-2 py-1.5 transition-colors hover:bg-muted"
            >
              {a.label}
            </a>
          ))}
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}

/** The link button beside a description: a list of the community's links to put at the caret. */
function LinkMenu({ onPick }: { onPick: (link: string) => void }) {
  const [open, setOpen] = useState(false)

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button
          type="button"
          aria-label="Insert a link"
          onMouseDown={(e) => e.preventDefault()}
          className="flex items-center gap-0.5 rounded-sm px-1 py-0.5 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
        >
          <Link2 className="size-4" />
          <ChevronDown className="size-3" />
        </button>
      </Popover.Trigger>
      <PopoverPortal>
        <Popover.Content
          align="end"
          sideOffset={4}
          onOpenAutoFocus={(e) => e.preventDefault()}
          onCloseAutoFocus={(e) => e.preventDefault()}
          className="z-50 flex w-52 flex-col rounded-sm border border-(length:--hairline) bg-popover p-1 text-popover-foreground shadow-sm"
          style={{ fontSize: 'var(--text-small)' }}
        >
          {AUTO_LINKS.map((link) => (
            <button
              key={link}
              type="button"
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => {
                setOpen(false)
                onPick(link)
              }}
              className="rounded-sm px-2 py-1.5 text-left transition-colors hover:bg-muted"
            >
              {link}
            </button>
          ))}
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}

/** The world lists: a list's name picks the whole list (shuffle), its arrow opens its worlds to pick one. */
function WorldListMenu({
  lists,
  seenWorlds,
  pickedWorld,
  listId,
  onList,
  onWorld,
  onSeenOpen,
}: {
  lists: WorldList[]
  /** The Seen list's worlds once loaded; null until then. */
  seenWorlds: WorldListWorld[] | null
  /** The single world picked, when the name is known. */
  pickedWorld: { worldId: string; name: string | null } | undefined
  listId: string | null
  onList: (id: string | null) => void
  onWorld: (id: string) => void
  /** The Seen arrow was opened: its worlds are not in `lists`, as it grows with every world Modbot sees. */
  onSeenOpen: () => void
}) {
  const list = lists.find((l) => l.id === listId)
  const shown = list?.name ?? pickedWorld?.name ?? pickedWorld?.worldId ?? '—'
  const item = 'flex cursor-default items-center justify-between gap-2 rounded-sm px-2 py-1.5 outline-none data-[highlighted]:bg-muted'
  const panel =
    'z-50 flex min-w-44 flex-col rounded-sm border border-(length:--hairline) bg-popover p-1 text-popover-foreground shadow-sm'

  return (
    <>
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <button
          type="button"
          aria-label="World list"
          className="flex h-(--control-h) w-full items-center justify-between rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 text-left"
        >
          <span className="truncate">{shown}</span>
          <ChevronDown className="size-4 text-muted-foreground" />
        </button>
      </DropdownMenu.Trigger>
      <MenuPortal>
        <DropdownMenu.Content align="start" sideOffset={4} className={panel} style={{ fontSize: 'var(--text-small)' }}>
          <DropdownMenu.Item className={item} onSelect={() => onList(null)}>
            —
          </DropdownMenu.Item>
          {lists.map((l) => {
            // Seen's worlds are null until its arrow has been opened once.
            const worlds = l.seen ? seenWorlds : l.worlds
            // The name picks the whole list (shuffle); the arrow, which a tap opens too, lists its worlds.
            return (
              <div key={l.id} className="flex items-stretch">
                <DropdownMenu.Item className={`${item} flex-1`} onSelect={() => onList(l.id)}>
                  {l.name}
                </DropdownMenu.Item>
                <DropdownMenu.Sub onOpenChange={l.seen ? (open) => open && onSeenOpen() : undefined}>
                  <DropdownMenu.SubTrigger aria-label={`${l.name} worlds`} className={`${item} w-9 justify-center`}>
                    <ChevronRight className="size-3.5 text-muted-foreground" />
                  </DropdownMenu.SubTrigger>
                  <MenuPortal>
                    <DropdownMenu.SubContent
                      sideOffset={4}
                      collisionPadding={8}
                      className={`${panel} max-h-64 max-w-(--radix-dropdown-menu-content-available-width) overflow-y-auto`}
                      style={{ fontSize: 'var(--text-small)' }}
                    >
                      {worlds === null ? (
                        <DropdownMenu.Item disabled className={item}>
                          …
                        </DropdownMenu.Item>
                      ) : (
                        worlds.map((w) => (
                          <DropdownMenu.Item
                            key={w.worldId}
                            title={w.name ?? w.worldId}
                            className={`${item} justify-start`}
                            onSelect={() => onWorld(w.worldId)}
                          >
                            {w.thumbnailUrl && <img src={w.thumbnailUrl} alt="" className="h-8 w-12 shrink-0 rounded-sm object-cover" />}
                            <span className="min-w-0 break-words">{w.name ?? w.worldId}</span>
                          </DropdownMenu.Item>
                        ))
                      )}
                    </DropdownMenu.SubContent>
                  </MenuPortal>
                </DropdownMenu.Sub>
              </div>
            )
          })}
        </DropdownMenu.Content>
      </MenuPortal>
    </DropdownMenu.Root>
    </>
  )
}
