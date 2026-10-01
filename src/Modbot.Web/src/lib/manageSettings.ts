import { createContext, useContext } from 'react'

/**
 * Whether the signed-in person may change Modbot's settings. A link to a settings tab (the calendar's
 * "Not set up") is only a link for somebody who can follow it; everybody else would land on a page
 * that has no tab for them. This only decides what to show: the server decides what is allowed.
 *
 * False until the app says otherwise, so a place that forgets to ask shows no link, not a dead one.
 */
export const ManageSettingsContext = createContext(false)

export function useCanManageSettings(): boolean {
  return useContext(ManageSettingsContext)
}
