import { NEEDS_VRCHAT_PLUS, VRCHAT_PLUS_URL } from '@/lib/calendar'

/**
 * The line beside a VRChat picture field that is turned off because the account Modbot signs in as
 * is known to have no VRChat+: the sentence, and where VRChat+ is had.
 */
export function VRChatPlusWarning() {
  return (
    <p className="text-warn" style={{ fontSize: 'var(--text-small)' }}>
      {NEEDS_VRCHAT_PLUS}{' '}
      <a className="underline" href={VRCHAT_PLUS_URL} target="_blank" rel="noreferrer noopener">
        Get VRChat+
      </a>
    </p>
  )
}
