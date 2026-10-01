// Relative, with the extension, so the Node test runner loads it as it is (see lib/nav.ts).
import type { DiscordBotHealth } from './api.ts'
import { discordState, vrchatState, type State } from './status.ts'

/** The bot's one word, as the bot state read and the Health read both say it. */
export type DiscordBotState = DiscordBotHealth['state']

/**
 * The Integrations page (2026-09-30): one card for each outside service Modbot is connected to, its
 * status, and where in Settings it is set up. The setting up stays in Settings; the page only
 * gathers what was spread over four of its topics. A later integration is one more entry here.
 *
 * Plain data, so the tests can load it without a rendering library behind it, like lib/status.ts.
 */

/** A Settings topic, as `/settings#<topic>` names it (pages/Settings.tsx). */
export type SettingsTopic = 'vrchat' | 'discord' | 'integrations' | 'proxy'

/** The address that opens a Settings topic. */
export function settingsPath(topic: SettingsTopic): string {
  return `/settings#${topic}`
}

/**
 * One thing set up for an integration, by name. `topic` when it is set up somewhere other than the
 * card's own Set up, so its name can lead there.
 */
export type Part = { name: string; topic?: SettingsTopic }

export type IntegrationId = 'vrchat' | 'discord' | 'email'

export type Integration = {
  id: IntegrationId
  name: string
  parts: readonly Part[]
  /** Where Set up leads. */
  topic: SettingsTopic
  state: State
}

/** Not set up yet. Not a fault, so it is not coloured like one, as the sidebar's rows do. */
export const NEEDS_SETUP: State = { label: 'Needs setup', tone: 'muted' }

/** A part that has not answered, or that this person may not read. Never shown as working. */
const UNKNOWN: State = { label: 'Unknown', tone: 'muted' }

/** What the page reads to say each card's status. Null is not known (yet). */
export type IntegrationReading = {
  /** The VRChat gate's status, the one the sidebar's VRChat row reads. */
  gate: string | null
  /** Whether a Discord bot token is saved (the onboarding status). */
  discordConfigured: boolean
  /**
   * The bot's own state, from the bot state read (it needs Change settings, as the page does).
   * Undefined when that read was not made or has not answered; null when no bot runs.
   */
  discordBot: DiscordBotState | null | undefined
  /** Whether a mail server is saved (the onboarding status, which the Email sending card reads). */
  smtpConfigured: boolean
}

/**
 * The integrations, in the order the sidebar puts VRChat and Discord, with Email after them.
 *
 * Each status in the sidebar's own words, starting with a capital as a label does, except that a
 * part that is not set up says "Needs setup" on every card alike.
 */
export function integrations(reading: IntegrationReading): Integration[] {
  return [
    {
      id: 'vrchat',
      name: 'VRChat',
      parts: [{ name: 'Account' }, { name: 'Group' }, { name: 'Proxy', topic: 'proxy' }],
      topic: 'vrchat',
      state: vrchatStatus(reading.gate),
    },
    {
      id: 'discord',
      name: 'Discord',
      parts: [{ name: 'Bot' }, { name: 'Server' }, { name: 'Channels' }, { name: 'Linking' }, { name: 'Roles' }],
      topic: 'discord',
      state: discordStatus(reading.discordConfigured, reading.discordBot),
    },
    {
      id: 'email',
      name: 'Email',
      parts: [{ name: 'Mail server' }, { name: 'Alerts' }],
      topic: 'integrations',
      state: reading.smtpConfigured ? { label: 'Working', tone: 'ok' } : NEEDS_SETUP,
    },
  ]
}

function vrchatStatus(gate: string | null): State {
  if (gate === null) return UNKNOWN
  if (gate === 'NotConfigured') return NEEDS_SETUP
  return capitalised(vrchatState(gate))
}

function discordStatus(configured: boolean, bot: DiscordBotState | null | undefined): State {
  if (!configured) return NEEDS_SETUP
  if (bot === undefined) return UNKNOWN
  if (bot === null || bot === 'NotConfigured') return NEEDS_SETUP
  return capitalised(discordState(bot))
}

function capitalised(state: State): State {
  return { ...state, label: state.label.charAt(0).toUpperCase() + state.label.slice(1) }
}
