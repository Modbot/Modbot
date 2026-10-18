import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CalendarEventInput } from '../src/lib/calendar.ts'
import {
  fieldsFromInput,
  inputFromTemplate,
  joinLength,
  lengthWords,
  MAX_LENGTH_MINUTES,
  splitLength,
} from '../src/lib/calendarTemplates.ts'

const input: CalendarEventInput = {
  title: 'Movie night',
  description: 'Bring snacks',
  startsAt: '2026-10-20T19:00',
  endsAt: '2026-10-20T21:00',
  timeZone: 'Europe/London',
  repeat: 'weekly',
  repeatDays: ['tuesday'],
  repeatEvery: 2,
  repeatUntil: '2026-12-31',
  repeatTimes: null,
  worldId: 'wrld_1',
  accessType: 'members',
  region: 'eu',
  imageUrl: null,
  vrChatImageId: 'file_1',
  category: 'hangout',
  languages: ['eng'],
  platforms: ['standalonewindows'],
  tags: ['movies'],
  visibility: 'group',
  notifyMembers: true,
  featured: true,
  publishToVRChat: true,
  publishToDiscord: true,
  postToChannel: false,
  channelId: null,
  autoOpen: true,
  openMinutesBefore: 15,
  draft: true,
  inviteHostUserId: null,
  inviteStaffUserIds: [],
  inviteListId: null,
  announceFirstJoinInDiscord: false,
  announceFirstJoinInVRChat: false,
  announceFirstJoinOnBluesky: false,
  mentionRoleId: null,
  postColour: null,
  blueskyPictures: null,
  blueskyVideo: null,
  blueskyGif: null,
  coverPictureId: null,
  vrChatTitle: 'Movies in VRChat',
  vrChatDescription: '',
  discordTitle: '',
  discordDescription: 'Pop in',
  googleTitle: '',
  googleDescription: '',
  googleColour: '',
  googleShowAs: 'free' as const,
  publishToGoogle: null,
  publishToBluesky: true,
  blueskyPostBeforeMinutes: 4320,
  blueskyWhoCanReply: ['anyone'],
  blueskyQuotesOff: false,
  blueskyLanguages: [],
  blueskyTitle: '',
  blueskyDescription: 'Doors at seven',
  categoryIds: [],
}

test('a template keeps no dates, no last date and no draft flag', () => {
  const fields = fieldsFromInput(input, 120)
  const keys = Object.keys(fields)

  for (const gone of ['startsAt', 'endsAt', 'timeZone', 'repeatUntil', 'draft']) {
    assert.ok(!keys.includes(gone), gone)
  }

  assert.equal(fields.lengthMinutes, 120)
  assert.equal(fields.title, 'Movie night')
  assert.equal(fields.repeat, 'weekly')
  assert.equal(fields.openMinutesBefore, 15)
})

test('a blank own text is saved as the shared one', () => {
  const fields = fieldsFromInput(input, 90)

  assert.equal(fields.vrChatTitle, 'Movies in VRChat')
  assert.equal(fields.discordDescription, 'Pop in')
})

test('a template keeps the Bluesky switch, the post time and its own text', () => {
  const loaded = inputFromTemplate(fieldsFromInput(input, 120), input)

  assert.equal(loaded.publishToBluesky, true)
  assert.equal(loaded.blueskyPostBeforeMinutes, 4320)
  assert.equal(loaded.blueskyDescription, 'Doors at seven')
})

test('a template saved before Bluesky loads with it off and the usual time', () => {
  const { publishToBluesky: _on, blueskyPostBeforeMinutes: _before, ...old } = fieldsFromInput(input, 120)
  const loaded = inputFromTemplate({ ...old, blueskyTitle: null, blueskyDescription: null }, input)

  assert.equal(loaded.publishToBluesky, false)
  assert.equal(loaded.blueskyPostBeforeMinutes, 1440)
})

test('a template keeps who can reply, quote posts and languages, and an older one loads with anyone, quotes allowed and no language', () => {
  const picked = fieldsFromInput(
    { ...input, blueskyWhoCanReply: ['followers', 'mentioned'], blueskyQuotesOff: true, blueskyLanguages: ['en', 'de'] },
    120,
  )
  const loaded = inputFromTemplate(picked, input)
  assert.deepEqual(loaded.blueskyWhoCanReply, ['followers', 'mentioned'])
  assert.equal(loaded.blueskyQuotesOff, true)
  assert.deepEqual(loaded.blueskyLanguages, ['en', 'de'])

  const { blueskyWhoCanReply: _who, blueskyQuotesOff: _quotes, blueskyLanguages: _langs, ...old } = picked
  const older = inputFromTemplate(old as typeof picked, input)
  assert.deepEqual(older.blueskyWhoCanReply, ['anyone'])
  assert.equal(older.blueskyQuotesOff, false)
  assert.deepEqual(older.blueskyLanguages, [])
})

test('a template keeps the Bluesky video, and one saved before videos loads with none', () => {
  const picked = fieldsFromInput({ ...input, blueskyVideo: { videoId: 'v1', url: null, alt: 'a cat' } }, 120)
  assert.deepEqual(inputFromTemplate(picked, input).blueskyVideo, { videoId: 'v1', url: null, alt: 'a cat' })

  const { blueskyVideo: _video, ...old } = picked
  assert.equal(inputFromTemplate(old as typeof picked, input).blueskyVideo, null)
})

test('a template keeps the Bluesky GIF, and one saved before GIFs loads with none', () => {
  const gif = { url: 'https://static.klipy.com/a.gif', alt: 'a cat' }
  const picked = fieldsFromInput({ ...input, blueskyGif: gif }, 120)
  assert.deepEqual(inputFromTemplate(picked, input).blueskyGif, gif)

  const { blueskyGif: _gif, ...old } = picked
  assert.equal(inputFromTemplate(old as typeof picked, input).blueskyGif, null)
})

test('a template keeps the post colour, and one saved before post colours loads with none', () => {
  const picked = fieldsFromInput({ ...input, postColour: 0x3498db }, 120)
  assert.equal(inputFromTemplate(picked, input).postColour, 0x3498db)

  const { postColour: _colour, ...old } = picked
  assert.equal(inputFromTemplate(old as typeof picked, input).postColour, null)
})

test('a template keeps its Bluesky pictures and their alt text, and one saved without them loads with none', () => {
  const pictures = [
    { pictureId: '11111111-1111-1111-1111-111111111111', alt: 'A stage' },
    { pictureId: '22222222-2222-2222-2222-222222222222', alt: '' },
  ]
  const saved = fieldsFromInput({ ...input, blueskyPictures: pictures }, 120)
  assert.deepEqual(saved.blueskyPictures, pictures)
  assert.deepEqual(inputFromTemplate(saved, input).blueskyPictures, pictures)

  const { blueskyPictures: _pictures, ...old } = saved
  assert.equal(inputFromTemplate(old as typeof saved, { ...input, blueskyPictures: pictures }).blueskyPictures, null)
})

test('a template loads into the form over the dates it is given', () => {
  const base: CalendarEventInput = { ...input, title: '', startsAt: '2027-01-05T10:00', endsAt: '2027-01-05T11:00', timeZone: 'UTC' }
  const loaded = inputFromTemplate(fieldsFromInput(input, 120), base)

  assert.equal(loaded.startsAt, '2027-01-05T10:00')
  assert.equal(loaded.endsAt, '2027-01-05T11:00')
  assert.equal(loaded.timeZone, 'UTC')
  assert.equal(loaded.title, 'Movie night')
  assert.equal(loaded.repeatUntil, null)
  assert.equal(loaded.draft, false)
  assert.deepEqual(loaded.repeatDays, ['tuesday'])
})

test('a template keeps the Google colour and busy, and one saved before them loads with the calendar default and free', () => {
  const picked = fieldsFromInput({ ...input, googleColour: '9', googleShowAs: 'busy' }, 120)
  assert.equal(inputFromTemplate(picked, input).googleColour, '9')
  assert.equal(inputFromTemplate(picked, input).googleShowAs, 'busy')

  const { googleColour: _colour, googleShowAs: _showAs, ...old } = picked
  assert.equal(inputFromTemplate(old as typeof picked, input).googleColour, '')
  assert.equal(inputFromTemplate(old as typeof picked, input).googleShowAs, 'free')
  assert.equal(inputFromTemplate({ ...picked, googleColour: null, googleShowAs: null }, input).googleShowAs, 'free')
})

test('a template round trip keeps what the form holds', () => {
  const again = fieldsFromInput(inputFromTemplate(fieldsFromInput(input, 120), input), 120)

  assert.deepEqual(again, fieldsFromInput(input, 120))
})

test('a length splits into hours and minutes and joins back', () => {
  assert.deepEqual(splitLength(150), { hours: 2, minutes: 30 })
  assert.equal(joinLength(2, 30), 150)
  assert.equal(joinLength(-1, 5), 5)
  assert.equal(joinLength(1.4, 0), 60)
  assert.equal(joinLength(0, MAX_LENGTH_MINUTES), MAX_LENGTH_MINUTES)
})

test('a length in words', () => {
  assert.equal(lengthWords(45), '45 min')
  assert.equal(lengthWords(120), '2 h')
  assert.equal(lengthWords(90), '1 h 30 min')
  assert.equal(lengthWords(24 * 60), '1 day')
  assert.equal(lengthWords(3 * 24 * 60), '3 days')
  assert.equal(lengthWords(MAX_LENGTH_MINUTES), '7 days')
})
