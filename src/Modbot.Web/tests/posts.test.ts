import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  blankPost,
  blueskyCardPictureWanted,
  blueskyCount,
  blueskyText,
  graphemes,
  canCancel,
  canEditWhole,
  discordCount,
  discordMessage,
  duplicateOf,
  headline,
  listsOf,
  localInput,
  inputFrom,
  requestOf,
  shownHeadline,
  shownLabel,
  vrchatAudience,
  vrchatCount,
  vrchatImage,
  vrchatNeedsTitle,
  vrchatPictureWanted,
  vrchatTitle,
  type Post,
  type PostDestination,
} from '../src/lib/postRules.ts'

const now = new Date('2026-10-03T18:00:00Z')

function destination(state: PostDestination['state'], more: Partial<PostDestination> = {}): PostDestination {
  return {
    id: 'd1',
    network: 'discord',
    target: '222',
    targetName: 'news',
    roleId: null,
    roleName: null,
    publish: false,
    state,
    shown: state === 'checking' ? 'sending' : state,
    link: null,
    externalId: null,
    error: null,
    errorAt: null,
    notPublished: false,
    mayBeSent: false,
    titleOverride: null,
    textOverride: null,
    sentText: null,
    sentAt: null,
    postedAt: null,
    publishedAt: null,
    vrChat: null,
    missingPermission: null,
    ...more,
  }
}

function post(status: Post['status'], destinations: PostDestination[], more: Partial<Post> = {}): Post {
  return {
    id: 'p1',
    title: 'Movie night',
    text: 'Friday at eight.\nBring snacks.',
    pictureId: null,
    status,
    lists: [],
    sendAt: '2026-10-04T18:00:00Z',
    sendAtLocal: '2026-10-04T18:00',
    timeZone: 'UTC',
    eventId: null,
    eventTitle: null,
    kind: null,
    version: 3,
    createdBy: 'ada',
    createdAt: '2026-10-03T12:00:00Z',
    updatedAt: '2026-10-03T12:00:00Z',
    cancelledAt: null,
    destinations,
    ...more,
  }
}

test('a post is in the list its destinations say, the server rule word for word', () => {
  assert.deepEqual(listsOf(post('scheduled', [destination('waiting')])), ['scheduled'])
  assert.deepEqual(listsOf(post('scheduled', [destination('checking')])), ['scheduled'])
  assert.deepEqual(listsOf(post('scheduled', [destination('posted')])), ['sent'])
  assert.deepEqual(listsOf(post('scheduled', [destination('removed')])), ['sent'])
  assert.deepEqual(listsOf(post('scheduled', [destination('failed')])), ['failed'])
  assert.deepEqual(listsOf(post('draft', [destination('waiting')])), ['drafts'])
  assert.deepEqual(listsOf(post('cancelled', [destination('skipped')])), ['cancelled'])
})

test('a post failed on one site and waiting on another is in both lists', () => {
  const p = post('scheduled', [destination('failed'), { ...destination('waiting'), id: 'd2', network: 'vrchat' }])
  assert.deepEqual(listsOf(p), ['scheduled', 'failed'])
})

test('a scheduled post whose every site was skipped is in none of the four lists', () => {
  assert.deepEqual(listsOf(post('scheduled', [destination('skipped')])), [])
})

test('a badge says what the list shows, and Not published for a post that went in but was not published', () => {
  assert.equal(shownLabel({ shown: 'paused', notPublished: false }), 'Paused')
  assert.equal(shownLabel({ shown: 'notSetUp', notPublished: false }), 'Not set up')
  assert.equal(shownLabel({ shown: 'sending', notPublished: false }), 'Sending…')
  assert.equal(shownLabel({ shown: 'removed', notPublished: false }), 'Deleted')
  assert.equal(shownLabel({ shown: 'posted', notPublished: true }), 'Not published')
})

test('the Discord message is the mention, the bold title and the text, as the server builds it', () => {
  assert.equal(discordMessage('Movie night', 'Bring snacks', null), '**Movie night**\nBring snacks')
  assert.equal(discordMessage('', 'Bring snacks', '42'), '<@&42>\nBring snacks')
  assert.equal(discordMessage('  ', ' one\r\ntwo ', null), 'one\ntwo')
})

test('the Discord counter counts the mention and the title, and turns red past 2000', () => {
  const input = { ...blankPost(now, 'UTC'), title: '', text: 'a'.repeat(2000) }
  assert.deepEqual(discordCount(input), { label: 'Discord 2000 / 2000', over: false })

  const titled = { ...input, title: 'Hi' }
  assert.equal(discordCount(titled).over, true)
})

test('an emoji counts as the two units Discord counts', () => {
  const input = { ...blankPost(now, 'UTC'), text: '\u{1F600}'.repeat(1000) }
  assert.equal(discordCount(input).label, 'Discord 2000 / 2000')
})

test('Discord counts its own text once Own text is ticked', () => {
  const input = blankPost(now, 'UTC')
  const own = { ...input, text: 'long '.repeat(500), discord: { ...input.discord, ownText: true, text: 'short' } }
  assert.equal(discordCount(own).label, 'Discord 5 / 2000')
})

test('a new post ticks nothing, so nothing goes anywhere unless somebody ticks it', () => {
  const input = blankPost(now, 'Europe/London')
  assert.equal(input.discord.on, false)
  assert.equal(requestOf(input, false).discord, null)
  assert.equal(input.timeZone, 'Europe/London')
})

test('a ticked Discord sends its section, and its own text only when ticked', () => {
  const input = blankPost(now, 'UTC')
  const ticked = { ...input, text: 'Hello', discord: { ...input.discord, on: true, channelId: '222', text: 'ignored' } }
  assert.deepEqual(requestOf(ticked, false).discord, { channelId: '222', roleId: null, publish: false, text: null })
})

test('Now sends no time; Later sends the picked one', () => {
  const input = { ...blankPost(now, 'UTC'), sendAt: '2026-10-04T20:00' }
  assert.equal(requestOf({ ...input, when: 'now' }, false).sendAt, null)
  assert.equal(requestOf(input, false).sendAt, '2026-10-04T20:00')
})

test('Duplicate keeps the words and the Discord choices, and unticks every site', () => {
  const original = post('scheduled', [destination('posted', { roleId: '42', textOverride: 'Own words' })])
  const copy = duplicateOf(original, now, 'UTC')

  assert.equal(copy.title, 'Movie night')
  assert.equal(copy.discord.on, false)
  assert.equal(copy.discord.channelId, '222')
  assert.equal(copy.discord.roleId, '42')
  assert.equal(copy.discord.ownText, true)
})

test('a post on its way, gone out, or maybe on a site cannot be changed whole', () => {
  assert.equal(canEditWhole(post('scheduled', [destination('waiting')])), true)
  assert.equal(canEditWhole(post('scheduled', [destination('sending')])), false)
  assert.equal(canEditWhole(post('scheduled', [destination('posted')])), false)
  assert.equal(canEditWhole(post('scheduled', [destination('failed', { mayBeSent: true })])), false)
  assert.equal(canEditWhole(post('scheduled', [destination('failed')])), true)
})

test('Cancel post is offered only while something has not gone', () => {
  assert.equal(canCancel(post('scheduled', [destination('waiting')])), true)
  assert.equal(canCancel(post('scheduled', [destination('posted')])), false)
  assert.equal(canCancel(post('draft', [destination('waiting')])), false)
})

test('a post with no title is named by its first line', () => {
  assert.equal(headline({ title: null, text: 'First line\nSecond' }), 'First line')
  assert.equal(headline({ title: '  Movie night ', text: 'x' }), 'Movie night')
})

test('a post that went out is named by what the sites show, once all of them agree', () => {
  // Not sent yet: the post's own title, whatever a site's own title is.
  assert.equal(shownHeadline(post('scheduled', [destination('waiting', { titleOverride: 'Own' })])), 'Movie night')

  // Edited on its one site: the new title.
  assert.equal(shownHeadline(post('scheduled', [destination('posted', { titleOverride: 'Movie night, edited' })])), 'Movie night, edited')

  // Edited on both: the new title.
  assert.equal(
    shownHeadline(
      post('scheduled', [
        destination('posted', { titleOverride: 'Cinema' }),
        destination('posted', { id: 'd2', network: 'vrchat', titleOverride: 'Cinema' }),
      ]),
    ),
    'Cinema',
  )

  // Edited on one of two: the sites differ, so the post's own.
  assert.equal(
    shownHeadline(
      post('scheduled', [
        destination('posted', { titleOverride: 'Cinema' }),
        destination('posted', { id: 'd2', network: 'vrchat' }),
      ]),
    ),
    'Movie night',
  )

  // A site that is not posted has no say.
  assert.equal(
    shownHeadline(
      post('scheduled', [
        destination('posted', { titleOverride: 'Cinema' }),
        destination('failed', { id: 'd2', network: 'vrchat' }),
      ]),
    ),
    'Cinema',
  )

  // No title anywhere: the first line of what the site shows.
  assert.equal(
    shownHeadline(post('scheduled', [destination('posted', { textOverride: 'New first line\nMore' })], { title: null })),
    'New first line',
  )
})

test('a time is written as a datetime field holds it, in the zone asked for', () => {
  assert.equal(localInput(new Date('2026-10-03T18:05:00Z'), 'UTC'), '2026-10-03T18:05')
  assert.equal(localInput(new Date('2026-10-03T18:05:00Z'), 'Asia/Tokyo'), '2026-10-04T03:05')
})

// ── VRChat (posts design §3.6) ───────────────────────────────────────────────────────────────

test('a new post leaves VRChat unticked, for the group, with nobody notified', () => {
  const input = blankPost(now, 'UTC')
  assert.equal(input.vrChat.on, false)
  assert.equal(input.vrChat.visibility, 'group')
  assert.equal(input.vrChat.notify, false)
  assert.equal(requestOf(input, false).vrChat, null)
})

test('VRChat needs a title: its own when ticked and filled, otherwise the post’s', () => {
  const input = blankPost(now, 'UTC')
  input.vrChat.on = true
  assert.equal(vrchatNeedsTitle(input), true)

  input.title = 'Movie night'
  assert.equal(vrchatNeedsTitle(input), false)
  assert.equal(vrchatTitle(input), 'Movie night')

  input.vrChat.ownTitle = true
  input.vrChat.title = '  '
  assert.equal(vrchatTitle(input), 'Movie night')

  input.vrChat.title = 'Cinema'
  assert.equal(vrchatTitle(input), 'Cinema')

  input.vrChat.on = false
  input.title = ''
  assert.equal(vrchatNeedsTitle(input), false)
})

test('the VRChat counter counts the text VRChat gets and is never red', () => {
  const input = blankPost(now, 'UTC')
  input.text = ' Friday at eight. '
  input.vrChat.on = true
  assert.deepEqual(vrchatCount(input), { label: 'VRChat 16', over: false })

  input.vrChat.ownText = true
  input.vrChat.text = 'x'.repeat(5000)
  assert.deepEqual(vrchatCount(input), { label: 'VRChat 5000', over: false })
})

test('a ticked VRChat sends who sees it, its roles only for the group, and its own words only when ticked', () => {
  const input = blankPost(now, 'UTC')
  input.vrChat = { ...input.vrChat, on: true, roleIds: ['grol_a'], notify: true }

  assert.deepEqual(requestOf(input, false).vrChat, {
    visibility: 'group',
    roleIds: ['grol_a'],
    notify: true,
    title: null,
    text: null,
    imageId: null,
  })

  input.vrChat = { ...input.vrChat, visibility: 'public', ownTitle: true, title: 'Cinema', ownText: true, text: 'Own words' }
  const sent = requestOf(input, false).vrChat
  assert.deepEqual(sent?.roleIds, [])
  assert.equal(sent?.title, 'Cinema')
  assert.equal(sent?.text, 'Own words')
})

test('the VRChat picture goes only while uploads are on and only for the picture the post has', () => {
  const input = blankPost(now, 'UTC')
  input.vrChat = { ...input.vrChat, on: true, imageId: 'file_1', imagePictureId: 'pic-1' }
  input.pictureId = 'pic-1'

  assert.equal(vrchatImage(input, true), 'file_1')
  assert.equal(vrchatImage(input, false), null)
  assert.equal(requestOf(input, false, null, true).vrChat?.imageId, 'file_1')
  assert.equal(requestOf(input, false, null, false).vrChat?.imageId, null)

  input.pictureId = 'pic-2'
  assert.equal(vrchatImage(input, true), null)
  assert.equal(vrchatPictureWanted(input, true), true)
  assert.equal(vrchatPictureWanted(input, false), false)
})

test('a saved VRChat destination comes back into the composer as it was saved', () => {
  const saved = post('scheduled', [
    destination('waiting', {
      network: 'vrchat',
      target: 'grp_1',
      targetName: null,
      titleOverride: 'Cinema',
      vrChat: { visibility: 'group', roleIds: ['grol_a'], roleNames: ['Members'], notify: true, imageId: 'file_1', pictureId: 'pic-1' },
    }),
  ])

  const input = inputFrom(saved, now, 'UTC')
  assert.equal(input.vrChat.on, true)
  assert.equal(input.vrChat.ownTitle, true)
  assert.equal(input.vrChat.title, 'Cinema')
  assert.equal(input.vrChat.ownText, false)
  assert.deepEqual(input.vrChat.roleIds, ['grol_a'])
  assert.equal(input.vrChat.imageId, 'file_1')
  assert.equal(input.discord.on, false)

  const copy = duplicateOf(saved, now, 'UTC')
  assert.equal(copy.vrChat.on, false)
  assert.equal(copy.vrChat.notify, false)
})

test('who sees a VRChat post is said as Everyone, Group, or its roles', () => {
  assert.equal(vrchatAudience('public', ['Members']), 'Everyone')
  assert.equal(vrchatAudience('group', []), 'Group')
  assert.equal(vrchatAudience('group', ['Members', 'Staff']), 'Members, Staff')
})

test('Bluesky counts characters as a reader sees them, not as UTF-16 units', () => {
  assert.equal(graphemes('Café'), 4)
  assert.equal(graphemes('\u{1F44D}\u{1F3FD}'), 1)
  assert.equal(graphemes('\u{1F468}‍\u{1F469}‍\u{1F467}‍\u{1F466}'), 1)
  assert.equal(graphemes('映画の夜'), 4)
  assert.equal(graphemes(''), 0)
})

test('Bluesky gets the title on its own line, then the text, or its own text alone', () => {
  const input = { ...blankPost(now, 'UTC'), title: 'Movie night', text: 'Friday at eight.\r\nBring snacks.' }

  assert.equal(blueskyText(input), 'Movie night\nFriday at eight.\nBring snacks.')
  assert.equal(blueskyText({ ...input, bluesky: { ...input.bluesky, ownText: true, text: '  Just this. ' } }), 'Just this.')
})

test('the Bluesky counter says 300 and turns red past it, or past 3000 bytes', () => {
  const input = { ...blankPost(now, 'UTC'), title: '', text: '\u{1F44D}\u{1F3FD}'.repeat(300) }
  assert.deepEqual(blueskyCount(input), { label: 'Bluesky 300 / 300', over: false })

  assert.equal(blueskyCount({ ...input, text: 'a'.repeat(301) }).over, true)

  // 121 family emoji: 121 characters, 3025 bytes.
  const families = '\u{1F468}‍\u{1F469}‍\u{1F467}‍\u{1F466}'.repeat(121)
  assert.deepEqual(blueskyCount({ ...input, text: families }), { label: 'Bluesky 121 / 300', over: true })
})

test("a ticked Bluesky sends its section, its own text only when ticked, and the card picture only for the post's picture", () => {
  const input = { ...blankPost(now, 'UTC'), pictureId: 'pic-1' }
  assert.equal(requestOf(input, false).bluesky, null)

  const ticked = { ...input, bluesky: { ...input.bluesky, on: true, cardPictureId: 'copy-1', cardPictureFrom: 'pic-1' } }
  assert.deepEqual(requestOf(ticked, false).bluesky, { text: null, cardPictureId: 'copy-1', cardPictureFrom: 'pic-1' })

  const stale = { ...ticked, pictureId: 'pic-2' }
  assert.deepEqual(requestOf(stale, false).bluesky, { text: null, cardPictureId: null, cardPictureFrom: null })
  assert.equal(blueskyCardPictureWanted(stale), true)
  assert.equal(blueskyCardPictureWanted(ticked), false)

  const own = { ...ticked, bluesky: { ...ticked.bluesky, ownText: true, text: 'Mine' } }
  assert.equal(requestOf(own, false).bluesky?.text, 'Mine')
})

test('a saved Bluesky destination comes back into the composer, and a duplicate unticks it', () => {
  const saved = post('scheduled', [
    destination('waiting', {
      network: 'bluesky',
      target: 'did:plc:ewvi7nxzyoun6zhxrhs64oiz',
      targetName: null,
      textOverride: 'Our own words',
      bluesky: { cardPictureId: 'copy-1', cardPictureFrom: 'pic-1' },
    }),
  ])

  const input = inputFrom(saved, now, 'UTC')
  assert.equal(input.bluesky.on, true)
  assert.equal(input.bluesky.ownText, true)
  assert.equal(input.bluesky.text, 'Our own words')
  assert.equal(input.bluesky.cardPictureId, 'copy-1')

  assert.equal(duplicateOf(saved, now, 'UTC').bluesky.on, false)
})
