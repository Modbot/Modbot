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
  mentionRoleId: null,
  coverPictureId: null,
  vrChatTitle: 'Movies in VRChat',
  vrChatDescription: '',
  discordTitle: '',
  discordDescription: 'Pop in',
  googleTitle: '',
  googleDescription: '',
  publishToGoogle: null,
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
