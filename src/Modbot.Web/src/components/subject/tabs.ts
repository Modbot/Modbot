/**
 * The tabs the world and instance popups know, and where a link to one that was taken out lands.
 *
 * Kept apart from the popups so the tests can read them: these values are in `?tab=` links people
 * have already pasted, and a tab dropped from a list without a line here would break them.
 *
 * `json` is still a tab on both. It is opened from the ⋯ menu rather than from the row, but its old
 * links open it as they always did.
 */

export const WORLD_TABS = ['overview', 'instances', 'history', 'json'] as const
export type WorldTab = (typeof WORLD_TABS)[number]

/** Metrics folded into Overview: the same four figures were already there, and its charts moved under them. */
export const WORLD_MOVED: Readonly<Record<string, WorldTab>> = { metrics: 'overview' }

export const INSTANCE_TABS = ['overview', 'people', 'logs', 'json'] as const
export type InstanceTab = (typeof INSTANCE_TABS)[number]
