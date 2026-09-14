/**
 * my.modbot.co, the project's instance selector (central services spec §2, §4.1).
 */
export const MY_MODBOT_ORIGIN = 'https://my.modbot.co'

/**
 * The register page for a Modbot address: it saves the address in the browser and notes it on
 * my.modbot.co. Defaults to the address this page was opened at.
 */
export function registerLink(instanceUrl: string = window.location.origin): string {
  return `${MY_MODBOT_ORIGIN}/register?url=${encodeURIComponent(instanceUrl)}`
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
