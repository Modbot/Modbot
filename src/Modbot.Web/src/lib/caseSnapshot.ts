/**
 * Reading the profile and membership stored on a case file.
 *
 * The snapshot is free-form JSON, written once and handed back exactly as it was written, so
 * nothing about its shape is guaranteed to a reader: a case file restored from a backup, written
 * by an older Modbot, or filled in by anything but the capture code can be missing any field in
 * it. Reading `.length` off an absent list throws, and a thrown render is a blank page for the
 * whole case file rather than one missing line.
 */

/** The words at a snapshot field — a profile's tags, a membership's roles. Anything else reads as none. */
export function textList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
}
