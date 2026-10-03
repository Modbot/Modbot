import assert from 'node:assert/strict'
import { test } from 'node:test'
import { integrations, settingsPath, type DiscordBotState, type IntegrationReading } from '../src/lib/integrations.ts'

function bot(state: DiscordBotState): DiscordBotState {
  return state
}

type GoogleReading = NonNullable<IntegrationReading['googleCalendar']>

const CHECKED: GoogleReading = {
  keyStored: true,
  calendarId: 'events@group.calendar.google.com',
  check: {
    at: '2026-10-03T12:00:00Z',
    calendarName: 'Group events',
    timeZone: 'Europe/London',
    canChangeEvents: true,
    public: 'all',
    problem: null,
  },
  sending: true,
}

type BlueskyReading = NonNullable<IntegrationReading['bluesky']>

const BLUESKY: BlueskyReading = {
  handle: 'ourgroup.bsky.social',
  appPasswordStored: true,
  signedInWithBluesky: false,
  check: { at: '2026-10-03T12:00:00Z', handle: 'ourgroup.bsky.social', displayName: 'Our group', automated: true, problem: null },
  posting: true,
}

function reading(over: Partial<IntegrationReading> = {}): IntegrationReading {
  return {
    gate: 'Working',
    discordConfigured: true,
    discordBot: bot('Connected'),
    smtpConfigured: true,
    googleCalendar: CHECKED,
    bluesky: BLUESKY,
    ...over,
  }
}

function stateOf(id: string, over: Partial<IntegrationReading> = {}) {
  return integrations(reading(over)).find((i) => i.id === id)?.state
}

test('VRChat, Discord, Email, Google Calendar and Bluesky, in that order', () => {
  assert.deepEqual(
    integrations(reading()).map((i) => i.name),
    ['VRChat', 'Discord', 'Email', 'Google Calendar', 'Bluesky'],
  )
})

test('everything set up and running says Working', () => {
  for (const item of integrations(reading())) assert.deepEqual(item.state, { label: 'Working', tone: 'ok' })
})

test('nothing set up says Needs setup on every card', () => {
  const none = reading({
    gate: 'NotConfigured',
    discordConfigured: false,
    discordBot: null,
    smtpConfigured: false,
    googleCalendar: { keyStored: false, calendarId: null, check: null },
    bluesky: { handle: null, appPasswordStored: false, signedInWithBluesky: false, check: null, posting: false },
  })
  for (const item of integrations(none)) assert.equal(item.state.label, 'Needs setup')
})

test("VRChat says what the sidebar's VRChat row says, and unknown until the gate answers", () => {
  assert.equal(stateOf('vrchat', { gate: 'NeedsOperator' })?.label, 'Needs you')
  assert.equal(stateOf('vrchat', { gate: 'WaitingOnPurpose' })?.tone, 'warn')
  assert.equal(stateOf('vrchat', { gate: null })?.label, 'Unknown')
})

test("Discord says the bot's own state, and never Working when the state could not be read", () => {
  assert.equal(stateOf('discord', { discordBot: bot('Failed') })?.label, 'Stopped, needs you')
  assert.equal(stateOf('discord', { discordBot: bot('Disconnected') })?.label, 'Reconnecting')
  assert.equal(stateOf('discord', { discordBot: undefined })?.label, 'Unknown')
  assert.equal(stateOf('discord', { discordConfigured: false, discordBot: undefined })?.label, 'Needs setup')
})

test('a saved bot token with no bot running, or a bot without its settings, says Needs setup', () => {
  assert.equal(stateOf('discord', { discordBot: null })?.label, 'Needs setup')
  assert.equal(stateOf('discord', { discordBot: bot('NotConfigured') })?.label, 'Needs setup')
})

test('each Set up leads to the Settings topic where it is set up', () => {
  const where = Object.fromEntries(integrations(reading()).map((i) => [i.id, settingsPath(i.topic)]))

  assert.deepEqual(where, {
    vrchat: '/settings#vrchat',
    discord: '/settings#discord',
    email: '/settings#integrations',
    google: '/settings#google',
    bluesky: '/settings#bluesky',
  })
})

test("the VRChat proxy is named on the VRChat card and leads to its own topic", () => {
  const vrchat = integrations(reading()).find((i) => i.id === 'vrchat')

  assert.deepEqual(vrchat?.parts.find((p) => p.name === 'Proxy'), { name: 'Proxy', topic: 'proxy' })
})

test('Google Calendar needs setup until a key and a calendar are saved and Check has run on them', () => {
  assert.equal(stateOf('google', { googleCalendar: { ...CHECKED, keyStored: false } })?.label, 'Needs setup')
  assert.equal(stateOf('google', { googleCalendar: { ...CHECKED, calendarId: null } })?.label, 'Needs setup')
  assert.equal(stateOf('google', { googleCalendar: { ...CHECKED, check: null } })?.label, 'Needs setup')
})

test('Google Calendar says Failed when Check found a problem, and Unknown when it could not be read', () => {
  const refused = { ...CHECKED, check: { ...CHECKED.check!, canChangeEvents: false, problem: 'Modbot can only read this calendar.' } }

  assert.deepEqual(stateOf('google', { googleCalendar: refused }), { label: 'Failed', tone: 'bad' })
  assert.equal(stateOf('google', { googleCalendar: undefined })?.label, 'Unknown')
})

test('the Google Calendar card names its key, its calendar and sending', () => {
  const google = integrations(reading()).find((i) => i.id === 'google')

  assert.deepEqual(google?.parts.map((p) => p.name), ['Key', 'Calendar', 'Sending'])
})

test('Google Calendar says Off while Sending is off, and Failed over Off when sending found a problem', () => {
  assert.deepEqual(stateOf('google', { googleCalendar: { ...CHECKED, sending: false } }), { label: 'Off', tone: 'muted' })
  assert.equal(stateOf('google', { googleCalendar: { ...CHECKED, sending: undefined } })?.label, 'Off')

  const refused = { ...CHECKED, sending: false, check: { ...CHECKED.check!, problem: 'Google did not accept the key.' } }
  assert.equal(stateOf('google', { googleCalendar: refused })?.label, 'Failed')
})

test('the Bluesky card names its account and posting', () => {
  const bluesky = integrations(reading()).find((i) => i.id === 'bluesky')

  assert.deepEqual(bluesky?.parts.map((p) => p.name), ['Account', 'Posting'])
})

test('Bluesky needs setup until a handle and an app password are saved and Check has run on them', () => {
  assert.equal(stateOf('bluesky', { bluesky: { ...BLUESKY, handle: null } })?.label, 'Needs setup')
  assert.equal(stateOf('bluesky', { bluesky: { ...BLUESKY, appPasswordStored: false } })?.label, 'Needs setup')
  assert.equal(stateOf('bluesky', { bluesky: { ...BLUESKY, check: null } })?.label, 'Needs setup')
})

test('Bluesky signed in with Bluesky needs no app password', () => {
  const signedIn = { ...BLUESKY, appPasswordStored: false, signedInWithBluesky: true }

  assert.deepEqual(stateOf('bluesky', { bluesky: signedIn }), { label: 'Working', tone: 'ok' })
  assert.equal(stateOf('bluesky', { bluesky: { ...signedIn, check: null } })?.label, 'Needs setup')
})

test('Bluesky says Failed when Check found a problem, Off while Posting is off, Working while it posts', () => {
  const refused = { ...BLUESKY, check: { ...BLUESKY.check!, problem: 'Bluesky did not accept the handle or app password.' } }

  assert.deepEqual(stateOf('bluesky', { bluesky: refused }), { label: 'Failed', tone: 'bad' })
  assert.deepEqual(stateOf('bluesky', { bluesky: { ...BLUESKY, posting: false } }), { label: 'Off', tone: 'muted' })
  assert.deepEqual(stateOf('bluesky'), { label: 'Working', tone: 'ok' })
  assert.equal(stateOf('bluesky', { bluesky: { ...refused, posting: false } })?.label, 'Failed')
})

test('Bluesky says Unknown when its settings could not be read', () => {
  assert.equal(stateOf('bluesky', { bluesky: undefined })?.label, 'Unknown')
})
