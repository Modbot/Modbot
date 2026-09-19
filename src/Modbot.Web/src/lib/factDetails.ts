// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and these are the pieces of the
// sentences worth testing on their own.
import { duration } from './format.ts'

type Payload = Record<string, unknown> | null | undefined

/** A payload field, when it is text and not empty. */
function text(data: Payload, key: string): string | null {
  const value = data?.[key]
  return typeof value === 'string' && value.length > 0 ? value : null
}

/**
 * Which avatar somebody switched to, or null when the entry does not say.
 *
 * VRChat's log carries an avatar's **display name** and never an `avtr_…` id — ids are withheld
 * from clients on purpose, to make avatar ripping harder — so a name is the whole of what any
 * source can give, and two avatars called the same thing cannot be told apart. Null for a row
 * recorded before the name was kept, and for a log line whose two halves could not be split
 * against the roster; the entry then says only that the avatar changed, which is still true.
 */
export function avatarWorn(data: Payload): string | null {
  return text(data, 'avatarName')
}

/**
 * How long somebody had been in the instance when they were kicked, as the clause the entry shows.
 *
 * Null whenever Modbot does not know — which is whenever no moderator's companion was reporting
 * that instance at the time. The clause is simply absent then; there is no "unknown" and no zero
 * standing in for one, because a made-up duration in a moderation record is worse than an absent
 * one.
 *
 * `seenArriving` is the difference between a measurement and a lower bound. A companion that
 * watched them walk in knows exactly how long they were there. A companion that arrived later and
 * found them already present knows only that they had been there *at least* that long, and the
 * sentence says so rather than rounding the distinction away.
 */
export function timeInInstance(data: Payload): string | null {
  const seconds = data?.['inInstanceSeconds']

  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0) return null

  return data?.['seenArriving'] === true
    ? `after ${duration(seconds)} in the instance`
    : `after at least ${duration(seconds)} in the instance`
}
