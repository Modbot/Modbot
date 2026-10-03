// The Marketing tab's posts (posts design §4): their shapes as the server sends them, the
// composer's own state, and the rules the page draws with. Nothing here talks to the server or the
// page, so the Node test runner loads this file as it is; lib/posts.ts has the calls.

export type PostStatus = 'draft' | 'scheduled' | 'cancelled'

/** The Marketing tab's lists, and the Cancelled view (posts design §2.3). */
export type PostListName = 'scheduled' | 'sent' | 'drafts' | 'failed' | 'cancelled'

export type PostNetwork = 'discord' | 'vrchat' | 'bluesky'

export type PostDestinationState = 'waiting' | 'sending' | 'checking' | 'posted' | 'failed' | 'removed' | 'skipped'

/** What a destination shows: its state, or why a waiting one waits. Checking is shown as sending. */
export type PostShown = Exclude<PostDestinationState, 'checking'> | 'paused' | 'off' | 'notSetUp'

/** Who sees a VRChat group post: the group (or only the roles picked), or everyone on vrchat.com. */
export type VRChatVisibility = 'group' | 'public'

/** A VRChat destination's own choices. */
export type VRChatDestination = {
  visibility: VRChatVisibility
  roleIds: string[]
  roleNames: string[]
  notify: boolean
  /** The picture's VRChat file id, or null for text only. */
  imageId: string | null
  /** The post's picture the file id was uploaded from. */
  pictureId: string | null
}

export type PostDestination = {
  id: string
  network: PostNetwork
  target: string
  targetName: string | null
  roleId: string | null
  roleName: string | null
  publish: boolean
  state: PostDestinationState
  shown: PostShown
  link: string | null
  externalId: string | null
  error: string | null
  errorAt: string | null
  notPublished: boolean
  mayBeSent: boolean
  titleOverride: string | null
  textOverride: string | null
  sentText: string | null
  sentAt: string | null
  postedAt: string | null
  publishedAt: string | null
  /** A VRChat destination's own choices; null for another site. */
  vrChat: VRChatDestination | null
  /** The VRChat group permission Modbot's account was refused for lacking. */
  missingPermission: string | null
}

export type Post = {
  id: string
  title: string | null
  text: string
  pictureId: string | null
  status: PostStatus
  lists: PostListName[]
  sendAt: string | null
  sendAtLocal: string | null
  timeZone: string
  eventId: string | null
  eventTitle: string | null
  kind: string | null
  version: number
  createdBy: string | null
  createdAt: string
  updatedAt: string
  cancelledAt: string | null
  destinations: PostDestination[]
}

export type PostCounts = Record<PostListName, number>

export type PostSites = {
  paused: boolean
  discordOn: boolean
  discordSetUp: boolean
  vrChatOn: boolean
  vrChatSetUp: boolean
  /** VRChat picture uploads are on, so a VRChat post can carry the picture. */
  vrChatPictures: boolean
}

/** A role of the VRChat group, for choosing who a VRChat post is for. */
export type PostRoleChoice = { id: string; name: string }

export type PostList = {
  list: PostListName
  posts: Post[]
  counts: PostCounts
  page: number
  pageSize: number
  total: number
  canManage: boolean
  sites: PostSites
  now: string
  vrChatRoles: PostRoleChoice[]
}

export type DiscordPostPreview = {
  content: string
  title: string | null
  text: string
  roleName: string | null
  roleColour: number
  channelName: string | null
  pictureUrl: string | null
  length: number
  limit: number
  publish: boolean
}

export type VRChatPostPreview = {
  title: string | null
  text: string
  visibility: VRChatVisibility
  roleNames: string[]
  notify: boolean
  pictureUrl: string | null
  length: number
}

export type PostPreview = { discord: DiscordPostPreview | null; vrChat: VRChatPostPreview | null; problems: string[] }

export type PostHealthProblem = {
  postId: string
  title: string
  network: PostNetwork
  problem: 'failed' | 'checking'
  error: string | null
  at: string | null
  missingPermission: string | null
}

export type PostsHealth = {
  paused: boolean
  problems: PostHealthProblem[]
  holds: { network: PostNetwork; hold: 'off' | 'notSetUp'; waiting: number }[]
}

export type PostSettings = { paused: boolean; discord: boolean; vrChat: boolean }

/** The Discord section of the composer. */
export type DiscordInput = {
  /** The chip: off when the composer opens, every time, for a new post. */
  on: boolean
  channelId: string
  roleId: string
  publish: boolean
  /** "Own text" ticked: Discord gets `text` instead of the post's. */
  ownText: boolean
  text: string
}

/** The VRChat section of the composer (posts design §3.6). */
export type VRChatInput = {
  /** The chip: off when the composer opens, every time, for a new post. */
  on: boolean
  visibility: VRChatVisibility
  /** With Group, only these roles; none is every member. */
  roleIds: string[]
  /** Unticked to start. */
  notify: boolean
  /** "Own title" ticked: VRChat gets `title` instead of the post's. */
  ownTitle: boolean
  title: string
  /** "Own text" ticked: VRChat gets `text` instead of the post's. */
  ownText: boolean
  text: string
  /** The picture's VRChat file id, uploaded from `imagePictureId`. */
  imageId: string | null
  imagePictureId: string | null
}

/** The composer's state. */
export type PostInput = {
  title: string
  text: string
  pictureId: string | null
  when: 'now' | 'later'
  /** `yyyy-MM-ddTHH:mm` in `timeZone`. */
  sendAt: string
  timeZone: string
  eventId: string | null
  discord: DiscordInput
  vrChat: VRChatInput
}

/** What the server takes for a new post or a change. */
export type PostRequest = {
  title: string | null
  text: string
  pictureId: string | null
  when: 'now' | 'later'
  sendAt: string | null
  timeZone: string
  draft: boolean
  eventId: string | null
  discord: { channelId: string | null; roleId: string | null; publish: boolean; text: string | null } | null
  vrChat: {
    visibility: VRChatVisibility
    roleIds: string[]
    notify: boolean
    title: string | null
    text: string | null
    imageId: string | null
  } | null
  version: number | null
}

/** Discord's limit, in UTF-16 units as the server counts (posts design §4.3). */
export const DISCORD_LIMIT = 2000

export const NETWORK_LABEL: Record<PostNetwork, string> = {
  discord: 'Discord',
  vrchat: 'VRChat',
  bluesky: 'Bluesky',
}

export const LIST_LABEL: Record<Exclude<PostListName, 'cancelled'>, string> = {
  scheduled: 'Scheduled',
  sent: 'Sent',
  drafts: 'Drafts',
  failed: 'Failed',
}

const SHOWN_LABEL: Record<PostShown, string> = {
  waiting: 'Waiting',
  paused: 'Paused',
  off: 'Off',
  notSetUp: 'Not set up',
  sending: 'Sending…',
  posted: 'Posted',
  failed: 'Failed',
  removed: 'Deleted',
  skipped: 'Skipped',
}

/** A destination's state in words, as its badge says it. */
export function shownLabel(destination: Pick<PostDestination, 'shown' | 'notPublished'>): string {
  if (destination.shown === 'posted' && destination.notPublished) return 'Not published'
  return SHOWN_LABEL[destination.shown] ?? destination.shown
}

/** The tone of a destination's badge. */
export function shownTone(shown: PostShown): 'good' | 'warn' | 'bad' | 'quiet' {
  if (shown === 'posted') return 'good'
  if (shown === 'failed') return 'bad'
  if (shown === 'paused' || shown === 'off' || shown === 'notSetUp') return 'warn'
  return 'quiet'
}

/**
 * The lists a post appears in, the server's rule word for word (`PostRules.ListsOf`): Failed is not
 * exclusive, so a post that went to one site and failed on another is in Failed and in whatever
 * else it is.
 */
export function listsOf(post: Pick<Post, 'status'> & { destinations: Pick<PostDestination, 'state'>[] }): PostListName[] {
  if (post.status === 'draft') return ['drafts']
  if (post.status === 'cancelled') return ['cancelled']

  const states = post.destinations.map((d) => d.state)
  const lists: PostListName[] = []

  if (states.some((s) => s === 'waiting' || s === 'sending' || s === 'checking')) lists.push('scheduled')
  if (states.some((s) => s === 'failed')) lists.push('failed')
  if (
    states.length > 0 &&
    states.every((s) => s === 'posted' || s === 'removed' || s === 'skipped') &&
    states.some((s) => s === 'posted' || s === 'removed')
  ) {
    lists.push('sent')
  }

  return lists
}

/** The list to show a post in once it is saved: the first it is in. */
export function listAfterSave(post: Post): Exclude<PostListName, 'cancelled'> {
  const first = listsOf(post).find((l) => l !== 'cancelled')
  return first ?? 'scheduled'
}

/** The title, or the first line of the text, the way the list names a post. */
export function headline(post: Pick<Post, 'title' | 'text'>): string {
  const title = post.title?.trim()
  if (title) return title

  const first = post.text.split('\n', 1)[0].trim()
  return first.length <= 80 ? first : `${first.slice(0, 80)}…`
}

/** The server's `PostTexts.Tidy`: line ends made `\n`, and the whole trimmed. */
export function tidy(text: string | null | undefined): string {
  return (text ?? '').replace(/\r\n?/g, '\n').trim()
}

/**
 * The Discord message, the server's `PostTexts.Discord` word for word: the role mention on its own
 * line, the title in bold, then the text. The counter counts this, so the mention and the title
 * count too.
 */
export function discordMessage(title: string, text: string, roleId: string | null): string {
  const lines: string[] = []
  if (roleId?.trim()) lines.push(`<@&${roleId.trim()}>`)
  const bold = tidy(title).replace(/\n/g, ' ')
  if (bold) lines.push(`**${bold}**`)
  const body = tidy(text)
  if (body) lines.push(body)
  return lines.join('\n')
}

/** The counter under the text for Discord: "Discord 212 / 2000", red once over. */
export function discordCount(input: Pick<PostInput, 'title' | 'text' | 'discord'>): { label: string; over: boolean } {
  const text = input.discord.ownText ? input.discord.text : input.text
  const length = discordMessage(input.title, text, input.discord.roleId || null).length
  return { label: `Discord ${length} / ${DISCORD_LIMIT}`, over: length > DISCORD_LIMIT }
}

/** The title VRChat gets: its own when "Own title" is ticked and filled, otherwise the post's. Empty when none. */
export function vrchatTitle(input: Pick<PostInput, 'title' | 'vrChat'>): string {
  const own = input.vrChat.ownTitle ? tidy(input.vrChat.title).replace(/\n/g, ' ') : ''
  return own || tidy(input.title).replace(/\n/g, ' ')
}

/** Whether VRChat is ticked with no title to send: its chip says "Needs a title" until there is one. */
export function vrchatNeedsTitle(input: Pick<PostInput, 'title' | 'vrChat'>): boolean {
  return input.vrChat.on && vrchatTitle(input) === ''
}

/** The counter under the text for VRChat: "VRChat 212". VRChat documents no limit, so it is never red. */
export function vrchatCount(input: Pick<PostInput, 'text' | 'vrChat'>): { label: string; over: boolean } {
  const text = input.vrChat.ownText ? input.vrChat.text : input.text
  return { label: `VRChat ${tidy(text).length}`, over: false }
}

/**
 * The VRChat file id to send with the post: only while VRChat picture uploads are on, and only when
 * it was uploaded from the picture the post has now (decision 14). Otherwise VRChat gets text only.
 */
export function vrchatImage(input: Pick<PostInput, 'pictureId' | 'vrChat'>, picturesOn: boolean): string | null {
  if (!picturesOn || !input.pictureId) return null
  return input.vrChat.imagePictureId === input.pictureId ? input.vrChat.imageId : null
}

/** Whether the post's picture still has to go to VRChat before VRChat can carry it. */
export function vrchatPictureWanted(input: Pick<PostInput, 'pictureId' | 'vrChat'>, picturesOn: boolean): boolean {
  return input.vrChat.on && picturesOn && input.pictureId !== null && input.vrChat.imagePictureId !== input.pictureId
}

/** Who sees a VRChat post, in words: Everyone, Group, or the roles it is for. */
export function vrchatAudience(visibility: VRChatVisibility, roleNames: readonly string[]): string {
  if (visibility === 'public') return 'Everyone'
  return roleNames.length > 0 ? roleNames.join(', ') : 'Group'
}

/** `yyyy-MM-ddTHH:mm` for `at` in `zone`, as a datetime-local field holds it. */
export function localInput(at: Date, zone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: zone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(at)

  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '00'
  return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}`
}

/** A new post: nothing ticked, going later, an hour from now in the browser's zone (decision 13). */
export function blankPost(now: Date, zone: string): PostInput {
  return {
    title: '',
    text: '',
    pictureId: null,
    when: 'later',
    sendAt: localInput(new Date(now.getTime() + 60 * 60 * 1000), zone),
    timeZone: zone,
    eventId: null,
    discord: { on: false, channelId: '', roleId: '', publish: false, ownText: false, text: '' },
    vrChat: blankVRChat(),
  }
}

function blankVRChat(): VRChatInput {
  return {
    on: false,
    visibility: 'group',
    roleIds: [],
    notify: false,
    ownTitle: false,
    title: '',
    ownText: false,
    text: '',
    imageId: null,
    imagePictureId: null,
  }
}

/** A saved post in the composer, to change before it goes. Its sites stay ticked as saved. */
export function inputFrom(post: Post, now: Date, zone: string): PostInput {
  const discord = post.destinations.find((d) => d.network === 'discord')
  const vrchat = post.destinations.find((d) => d.network === 'vrchat')
  const blank = blankPost(now, zone)

  return {
    title: post.title ?? '',
    text: post.text,
    pictureId: post.pictureId,
    when: 'later',
    sendAt: post.sendAtLocal ?? blank.sendAt,
    timeZone: post.sendAtLocal ? post.timeZone : zone,
    eventId: post.eventId,
    discord: discord
      ? {
          on: true,
          channelId: discord.target,
          roleId: discord.roleId ?? '',
          publish: discord.publish,
          ownText: discord.textOverride !== null,
          text: discord.textOverride ?? '',
        }
      : blank.discord,
    vrChat: vrchat
      ? {
          on: true,
          visibility: vrchat.vrChat?.visibility ?? 'group',
          roleIds: [...(vrchat.vrChat?.roleIds ?? [])],
          notify: vrchat.vrChat?.notify ?? false,
          ownTitle: vrchat.titleOverride !== null,
          title: vrchat.titleOverride ?? '',
          ownText: vrchat.textOverride !== null,
          text: vrchat.textOverride ?? '',
          imageId: vrchat.vrChat?.imageId ?? null,
          imagePictureId: vrchat.vrChat?.pictureId ?? null,
        }
      : blank.vrChat,
  }
}

/**
 * Duplicate (decision 6): a new draft from an old post, with its words, picture and Discord
 * choices, and every site unticked, as every new post starts.
 */
export function duplicateOf(post: Post, now: Date, zone: string): PostInput {
  const copy = inputFrom(post, now, zone)
  return {
    ...copy,
    when: 'later',
    sendAt: blankPost(now, zone).sendAt,
    timeZone: zone,
    discord: { ...copy.discord, on: false },
    vrChat: { ...copy.vrChat, on: false, notify: false },
  }
}

/**
 * What the server is sent for the composer's state. `vrChatPictures` says whether VRChat picture
 * uploads are on: off, VRChat is sent no picture id.
 */
export function requestOf(input: PostInput, draft: boolean, version: number | null = null, vrChatPictures = false): PostRequest {
  return {
    title: input.title.trim() || null,
    text: input.text,
    pictureId: input.pictureId,
    when: input.when,
    sendAt: input.when === 'later' ? input.sendAt || null : null,
    timeZone: input.timeZone,
    draft,
    eventId: input.eventId,
    discord: input.discord.on
      ? {
          channelId: input.discord.channelId || null,
          roleId: input.discord.roleId || null,
          publish: input.discord.publish,
          text: input.discord.ownText ? input.discord.text : null,
        }
      : null,
    vrChat: input.vrChat.on
      ? {
          visibility: input.vrChat.visibility,
          roleIds: input.vrChat.visibility === 'group' ? [...input.vrChat.roleIds] : [],
          notify: input.vrChat.notify,
          title: input.vrChat.ownTitle ? input.vrChat.title : null,
          text: input.vrChat.ownText ? input.vrChat.text : null,
          imageId: vrchatImage(input, vrChatPictures),
        }
      : null,
    version,
  }
}

/** Whether the person may still change the whole post: not on its way, not gone out, not maybe on a site. */
export function canEditWhole(post: Post): boolean {
  if (post.status === 'cancelled') return false
  return post.destinations.every(
    (d) => d.state === 'waiting' || d.state === 'skipped' || (d.state === 'failed' && !d.mayBeSent),
  )
}

/** Whether Cancel post is offered: scheduled, not on its way, with something left that has not gone. */
export function canCancel(post: Post): boolean {
  if (post.status !== 'scheduled') return false
  if (post.destinations.some((d) => d.state === 'sending' || d.state === 'checking')) return false
  return post.destinations.some((d) => d.state === 'waiting' || (d.state === 'failed' && !d.mayBeSent))
}

/** Whether Post now is offered: a draft or a scheduled post with something not yet gone, and nothing on its way. */
export function canPostNow(post: Post): boolean {
  if (post.status === 'cancelled') return false
  if (post.destinations.some((d) => d.state === 'sending' || d.state === 'checking')) return false
  if (post.status === 'draft') return post.destinations.length > 0
  return post.destinations.some((d) => d.state === 'waiting' || (d.state === 'failed' && !d.mayBeSent))
}
