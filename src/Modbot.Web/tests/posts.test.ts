import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  blankPost,
  canCancel,
  canEditWhole,
  discordCount,
  discordMessage,
  duplicateOf,
  headline,
  listsOf,
  localInput,
  requestOf,
  shownLabel,
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

test('a time is written as a datetime field holds it, in the zone asked for', () => {
  assert.equal(localInput(new Date('2026-10-03T18:05:00Z'), 'UTC'), '2026-10-03T18:05')
  assert.equal(localInput(new Date('2026-10-03T18:05:00Z'), 'Asia/Tokyo'), '2026-10-04T03:05')
})
