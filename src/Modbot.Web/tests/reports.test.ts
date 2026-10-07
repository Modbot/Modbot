import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  aboutSubject,
  attachmentLine,
  badgeCount,
  closeProblem,
  CLOSE_NOTE_MAX,
  discordLink,
  hasQuote,
  onTab,
  openTabCount,
  personName,
  writtenText,
} from '../src/lib/reports.ts'
import type { MemberReportView, ReportMessage } from '../src/lib/api.ts'

const message = (over: Partial<ReportMessage> = {}): ReportMessage => ({
  channelId: '10',
  channelName: 'general',
  sentAt: '2026-10-07T10:00:00Z',
  text: 'some words',
  attachments: [],
  url: 'https://discord.com/channels/1/10/20',
  ...over,
})

const report = (over: Partial<MemberReportView> = {}): MemberReportView => ({
  id: 'r1',
  state: 'open',
  createdAt: '2026-10-07T10:05:00Z',
  reporter: { discordId: '111', name: 'Reporter' },
  about: { discordId: '222', name: 'Reported', vrchatUserId: null },
  text: 'They were rude.',
  message: null,
  textRemovedAt: null,
  closedAt: null,
  closedByUsername: null,
  closeNote: null,
  ...over,
})

test('the badge shows nothing at zero and nothing for a count that is not a number', () => {
  assert.equal(badgeCount(0), undefined)
  assert.equal(badgeCount(-3), undefined)
  assert.equal(badgeCount(Number.NaN), undefined)
  assert.equal(badgeCount(undefined), undefined)
  assert.equal(badgeCount(null), undefined)
  assert.equal(badgeCount(4), 4)
})

test('the Open tab carries a count only once the list has been read', () => {
  assert.equal(openTabCount(undefined), null)
  assert.equal(openTabCount(null), null)
  assert.equal(openTabCount(0), 0)
  assert.equal(openTabCount(7), 7)
  assert.equal(openTabCount(-1), 0)
})

test('a report about somebody linked to VRChat opens their VRChat profile, else their Discord one', () => {
  assert.deepEqual(aboutSubject({ discordId: '222', name: 'A', vrchatUserId: 'usr_x' }), { kind: 'person', id: 'usr_x' })
  assert.deepEqual(aboutSubject({ discordId: '222', name: 'A', vrchatUserId: null }), { kind: 'discord-person', id: '222' })
  assert.deepEqual(aboutSubject({ discordId: '222', name: 'A' }), { kind: 'discord-person', id: '222' })
})

test('a person is shown by name, else by id', () => {
  assert.equal(personName({ discordId: '222', name: 'Reported' }), 'Reported')
  assert.equal(personName({ discordId: '222', name: '  ' }), '222')
  assert.equal(personName({ discordId: '222', name: null }), '222')
})

test('a close needs a note that is not blank and not too long', () => {
  assert.equal(closeProblem(''), 'Write a note first.')
  assert.equal(closeProblem('   \n '), 'Write a note first.')
  assert.equal(closeProblem('Spoke to them.'), null)
  assert.equal(closeProblem('x'.repeat(CLOSE_NOTE_MAX)), null)
  assert.notEqual(closeProblem('x'.repeat(CLOSE_NOTE_MAX + 1)), null)
})

test('what was written is shown, and a removed text is null', () => {
  assert.equal(writtenText(report()), 'They were rude.')
  assert.equal(writtenText(report({ text: null, textRemovedAt: '2027-10-08T00:00:00Z' })), null)
  assert.equal(writtenText(report({ text: '  ' })), null)
})

test('a quote shows for a message with words, files or a channel, and not for none', () => {
  assert.equal(hasQuote(report()), false)
  assert.equal(hasQuote(report({ message: message() })), true)
  assert.equal(hasQuote(report({ message: message({ text: null, channelName: null, attachments: ['a.png'] }) })), true)
  assert.equal(hasQuote(report({ message: message({ text: ' ', channelName: null, attachments: [] }) })), false)
})

test('the attached files are named on one line', () => {
  assert.equal(attachmentLine(null), null)
  assert.equal(attachmentLine(message()), null)
  assert.equal(attachmentLine(message({ attachments: ['a.png', 'b.mp4'] })), 'a.png, b.mp4')
})

test('Open in Discord is offered only for an https message link', () => {
  assert.equal(discordLink(report()), null)
  assert.equal(discordLink(report({ message: message() })), 'https://discord.com/channels/1/10/20')
  assert.equal(discordLink(report({ message: message({ url: 'javascript:alert(1)' }) })), null)
  assert.equal(discordLink(report({ message: message({ url: null }) })), null)
})

test('a tab keeps only the reports that belong on it', () => {
  const list = [report({ id: 'a' }), report({ id: 'b', state: 'closed' }), report({ id: 'c' })]

  assert.deepEqual(onTab(list, 'open').map((r) => r.id), ['a', 'c'])
  assert.deepEqual(onTab(list, 'closed').map((r) => r.id), ['b'])
})
