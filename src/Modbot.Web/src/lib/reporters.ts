import type { AuditEntry } from '@/lib/api'

/**
 * The moderators whose clients reported a fact, in the order their reports arrived.
 *
 * A fact no client reported has none, which is most facts: a ban read out of VRChat's audit log or
 * a setting changed in Modbot has a source, not a reporter. A fact with several means several
 * clients independently saw the same thing and the later reports were folded into the one fact.
 *
 * A reporter whose account Modbot can no longer name is left out rather than shown as an id —
 * there is nothing a moderator can do with a device id, and it is still in the entry's payload.
 */
export function reporterNames(entry: Pick<AuditEntry, 'reportedBy'>): string[] {
  return (entry.reportedBy ?? []).map((r) => r.name).filter((name): name is string => !!name)
}
