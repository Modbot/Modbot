/**
 * my.modbot.co, the project's server selector (central services spec §2, §4.1).
 *
 * The address comes from the server, which reads `MODBOT_MY_URL`, so a group running its own
 * selector points every link at it with one variable. Until the first status has loaded — which is
 * before anything is rendered — this is the project's own.
 */
export const DEFAULT_MY_MODBOT_ORIGIN = 'https://my.modbot.co'

let origin = DEFAULT_MY_MODBOT_ORIGIN

/** Called once the server's onboarding status has been read. */
export function setMyModbotOrigin(url: string | null | undefined): void {
  if (url) origin = url.replace(/\/+$/, '')
}

export function myModbotOrigin(): string {
  return origin
}

/** The group this server manages, as the onboarding status last reported it. */
export type ServerGroup = {
  id: string
  name: string
  iconUrl?: string | null
  bannerUrl?: string | null
}

let group: ServerGroup | null = null

/**
 * Called alongside {@link setMyModbotOrigin}, from the same onboarding status.
 *
 * The status is already loaded before anything renders and already carries the group, so the
 * register link costs no round trip of its own. Reading `/api/server` instead would be a second
 * request for four values this page is holding.
 */
export function setServerGroup(next: ServerGroup | null | undefined): void {
  group = next ?? null
}

/**
 * The register page for a Modbot address: it saves the address in the browser and notes it on
 * my.modbot.co. Defaults to the address this page was opened at.
 *
 * The group travels in the link so the register page can show which group is being added before
 * it has talked to the server at all — and can still show it if the server is behind something
 * the browser cannot reach.
 */
export function registerLink(serverUrl: string = window.location.origin): string {
  const parameters = [`url=${encodeURIComponent(serverUrl)}`]

  if (group?.id) parameters.push(`groupId=${encodeURIComponent(group.id)}`)
  if (group?.name) parameters.push(`name=${encodeURIComponent(group.name)}`)
  if (group?.iconUrl) parameters.push(`icon=${encodeURIComponent(group.iconUrl)}`)
  if (group?.bannerUrl) parameters.push(`banner=${encodeURIComponent(group.bannerUrl)}`)

  return `${origin}/register?${parameters.join('&')}`
}

let opened = false

/**
 * Opens the register page in a new tab, at most once per page load.
 *
 * Must be called straight from a click or a form submit, before anything is awaited. A browser
 * only lets a page open a tab in direct response to a person's action, and blocks one opened after
 * a network round trip.
 */
export function openRegisterOnce(): void {
  if (opened) return
  opened = true
  window.open(registerLink(), '_blank', 'noopener')
}
