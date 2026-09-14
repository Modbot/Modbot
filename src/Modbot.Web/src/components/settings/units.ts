/**
 * Units and remembered inputs for the settings screens.
 *
 * Plain functions, kept out of the component files so the fast-refresh boundary stays intact —
 * a module that exports both components and helpers reloads the whole page on every edit.
 */

/** Binary, because that is how disks are sized and how most providers bill. */
export const GB = 1024 * 1024 * 1024

/** Megabytes in the form, bytes on the wire. Nobody types 104857600. */
export const MB = 1024 * 1024

export const DAY_MS = 24 * 60 * 60 * 1000

/** Gigabytes to a fixed number of decimals, for figures read off against a typed disk size. */
export function gigabytes(n: number, decimals = 3): string {
  return (n / GB).toFixed(decimals)
}

/**
 * How many years the disk must last at the current rate before the storage card says there is
 * plenty. Five years outlasts the disk or hosting plan most operators are on today, so nothing
 * inside that window is worth acting on now.
 */
export const PLENTY_OF_STORAGE_YEARS = 5

/**
 * Whether a disk size has been entered and, at the current rate, it does not fill within
 * {@link PLENTY_OF_STORAGE_YEARS}. No fill date with room left means the data is not growing, or
 * is growing too slowly to fill the disk within a century.
 */
export function hasPlentyOfStorage(
  storage: { bytes: number; capacityExhausted: string | null; measuredAt: string },
  capacityBytes: number | null,
): boolean {
  if (capacityBytes === null || storage.bytes >= capacityBytes) return false
  if (storage.capacityExhausted === null) return true

  const years =
    (Date.parse(storage.capacityExhausted) - Date.parse(storage.measuredAt)) / (365.2425 * DAY_MS)
  return years > PLENTY_OF_STORAGE_YEARS
}

export function bytes(n: number): string {
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = n / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`
}

export function seconds(n: number): string {
  if (n < 60) return `${n % 1 === 0 ? n : n.toFixed(1)}s`
  if (n < 3600) return `${(n / 60).toFixed(n % 60 === 0 ? 0 : 1)} min`
  return `${(n / 3600).toFixed(1)} h`
}

/**
 * The what-if inputs live in the browser because the server does not store them — nothing in
 * Modbot behaves differently for having been told a per-GB price. Remembering them here means
 * they survive a reload without a column and a migration existing to hold a number on a screen.
 */
export function remembered(key: string): string {
  try {
    return localStorage.getItem(key) ?? ''
  } catch {
    return ''
  }
}

export function remember(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    /* private windows and blocked site data are fine; the field just does not persist */
  }
}
