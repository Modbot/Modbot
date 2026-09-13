/**
 * The permission bits the SPA needs to know about, mirroring `ModbotPermissions`.
 *
 * Only the bits a screen decides something on. The server enforces every one of these on every
 * request regardless; this exists so a control the caller cannot use is not drawn, which is
 * kinder than drawing it and answering the click with a 403.
 *
 * BigInt, because the bitfield is a 64-bit long and `Administrator` is bit 62. A JavaScript
 * number keeps 53 bits, so an administrator's value arrives rounded -- which is why the check
 * for that one bit is "at least 2^62" rather than a mask.
 */
export const PERMISSION = {
  ViewProfile: 1n << 1n,
  EditAgeVerification: 1n << 18n,
} as const

const ADMINISTRATOR = 1n << 62n

export function hasPermission(bits: number | null | undefined, flag: bigint): boolean {
  if (bits === null || bits === undefined || !Number.isFinite(bits)) return false

  const held = BigInt(Math.trunc(bits))

  if (held >= ADMINISTRATOR) return true

  return (held & flag) !== 0n
}
