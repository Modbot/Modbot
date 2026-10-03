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
}

function reading(over: Partial<IntegrationReading> = {}): IntegrationReading {
  return {
    gate: 'Working',
    discordConfigured: true,
    discordBot: bot('Connected'),
    smtpConfigured: true,
    googleCalendar: CHECKED,
    ...over,
  }
}

function stateOf(id: string, over: Partial<IntegrationReading> = {}) {
  return integrations(reading(over)).find((i) => i.id === id)?.state
}

test('VRChat, Discord, Email and Google Calendar, in that order', () => {
  assert.deepEqual(
    integrations(reading()).map((i) => i.name),
    ['VRChat', 'Discord', 'Email', 'Google Calendar'],
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

test('the Google Calendar card names its key and its calendar', () => {
  const google = integrations(reading()).find((i) => i.id === 'google')

  assert.deepEqual(google?.parts.map((p) => p.name), ['Key', 'Calendar'])
})
