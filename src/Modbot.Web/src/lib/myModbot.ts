/**
 * my.modbot.co, the project's instance selector (central services spec §2, §4.1).
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

/**
 * The register page for a Modbot address: it saves the address in the browser and notes it on
 * my.modbot.co. Defaults to the address this page was opened at.
 */
export function registerLink(instanceUrl: string = window.location.origin): string {
  return `${origin}/register?url=${encodeURIComponent(instanceUrl)}`
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
