// Relative, with the extension, rather than the '@/' alias: the Node test runner resolves neither,
// and what the Reports page shows is worth a test. `api.ts` is imported for types only.
import type { MemberReportView, ReportMessage, ReportPerson } from './api.ts'

/** The longest close note the server takes. */
export const CLOSE_NOTE_MAX = 2000

/** The popup a person opens: their VRChat account when the Discord account was linked to one then. */
export function aboutSubject(about: ReportPerson): { kind: 'person' | 'discord-person'; id: string } {
  return about.vrchatUserId
    ? { kind: 'person', id: about.vrchatUserId }
    : { kind: 'discord-person', id: about.discordId }
}

/** What is shown for a person: the name, else the id. */
export function personName(person: ReportPerson): string {
  return person.name?.trim() || person.discordId
}

/** Why a close cannot go yet, or null. */
export function closeProblem(note: string): string | null {
  const text = note.trim()
  if (text.length === 0) return 'Write a note first.'
  if (text.length > CLOSE_NOTE_MAX) return `The note is too long (at most ${CLOSE_NOTE_MAX} characters).`
  return null
}

/** The number on the sidebar entry: nothing at zero, and nothing for a count that is not a real number. */
export function badgeCount(open: number | null | undefined): number | undefined {
  return typeof open === 'number' && Number.isFinite(open) && open > 0 ? Math.floor(open) : undefined
}

/** The open tab's label: its name, then how many are waiting once the list is read. */
export function openTabCount(openCount: number | null | undefined): number | null {
  return typeof openCount === 'number' && Number.isFinite(openCount) ? Math.max(0, Math.floor(openCount)) : null
}

/** The attached files' names on one line, or null when there are none. */
export function attachmentLine(message: ReportMessage | null): string | null {
  const names = (message?.attachments ?? []).filter((n) => n.trim().length > 0)
  return names.length === 0 ? null : names.join(', ')
}

/**
 * What the page shows of what was written: the words, or null once retention removed them (the
 * page then says so instead of showing an empty quote).
 */
export function writtenText(report: MemberReportView): string | null {
  return report.text && report.text.trim().length > 0 ? report.text : null
}

/** Whether there is anything quoted to show: the message's words, its files, or where it was. */
export function hasQuote(report: MemberReportView): boolean {
  const m = report.message
  if (!m) return false
  return Boolean(m.text?.trim()) || (m.attachments?.length ?? 0) > 0 || Boolean(m.channelName)
}

/** Only an https address is opened from the page. */
export function discordLink(report: MemberReportView): string | null {
  const url = report.message?.url
  return url && url.startsWith('https://') ? url : null
}

/** The list for a tab: the server already filters, so this keeps only what belongs on the tab it was asked for. */
export function onTab(reports: MemberReportView[], tab: 'open' | 'closed'): MemberReportView[] {
  return reports.filter((r) => r.state === tab)
}
