import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  createLiveStream,
  RULES,
  stateWord,
  type LiveEvent,
  type LivePollPage,
  type LiveState,
  type SocketLike,
} from '../src/lib/liveStream.ts'

/**
 * The stream's rules, with the browser taken out: a fake socket the test opens and drops, a fake
 * poll, and a clock and timers the test moves by hand.
 */

class FakeSocket implements SocketLike {
  onopen: (() => void) | null = null
  onmessage: ((message: { data: unknown }) => void) | null = null
  onclose: ((event: { code: number }) => void) | null = null
  onerror: (() => void) | null = null
  sent: string[] = []
  closed = false
  readonly address: string

  constructor(address: string) {
    this.address = address
  }

  send(data: string) {
    this.sent.push(data)
  }

  close() {
    this.closed = true
  }

  open() {
    this.onopen?.()
  }

  deliver(message: object) {
    this.onmessage?.({ data: JSON.stringify(message) })
  }

  drop(code = 1006) {
    this.onclose?.({ code })
  }
}

type Timer = { run: () => void; at: number }

function harness(options: { ticketFails?: boolean; ticketRefused?: boolean } = {}) {
  let now = 0
  const timers = new Map<number, Timer>()
  let nextTimer = 1
  const sockets: FakeSocket[] = []
  const polls: { after: string | null; wait: number; resolve: (page: LivePollPage) => void; reject: (e: Error) => void }[] = []
  const states: LiveState[] = []
  const events: LiveEvent[] = []

  const stream = createLiveStream({
    ticket: () =>
      options.ticketRefused
        ? Promise.reject(Object.assign(new Error('forbidden'), { fatal: true }))
        : options.ticketFails
          ? Promise.reject(new Error('no ticket'))
          : Promise.resolve('t-' + sockets.length),
    openSocket: (address) => {
      const socket = new FakeSocket(address)
      sockets.push(socket)
      return socket
    },
    socketAddress: () => 'wss://modbot.example/api/live/ws',
    poll: (after, wait) =>
      new Promise<LivePollPage>((resolve, reject) => {
        polls.push({ after, wait, resolve, reject })
      }),
    now: () => now,
    setTimer: (run, ms) => {
      const id = nextTimer++
      timers.set(id, { run, at: now + ms })
      return id
    },
    clearTimer: (handle) => {
      timers.delete(handle as number)
    },
    random: () => 1,
  })

  stream.onState((s) => states.push(s))
  stream.onEvent((e) => events.push(e))

  /** Moves the clock and runs every timer that came due, in order. */
  async function advance(ms: number) {
    now += ms
    for (;;) {
      const due = [...timers.entries()].filter(([, t]) => t.at <= now).sort((a, b) => a[1].at - b[1].at)[0]
      if (!due) break
      timers.delete(due[0])
      due[1].run()
      await settle()
    }
  }

  const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0))

  /** Answers the poll that is waiting. */
  async function answerPoll(page: LivePollPage) {
    polls[polls.length - 1].resolve(page)
    await settle()
  }

  async function failPoll() {
    polls[polls.length - 1].reject(new Error('down'))
    await settle()
  }

  return { stream, sockets, polls, states, events, advance, settle, answerPoll, failPoll, latest: () => sockets[sockets.length - 1] }
}

function event(id: string, kind = 'person_joined'): LiveEvent {
  return {
    id,
    cursor: id,
    kind,
    type: 'vrchat.instance.join',
    typeRaw: null,
    category: 'moderation',
    label: 'Joined an instance',
    source: 'Client',
    at: '2026-09-16T20:00:00+00:00',
    occurredBefore: null,
    observedAt: '2026-09-16T20:00:00+00:00',
    subject: { platform: 'VRChat', id: 'usr_' + id, kind: 'Person' },
    actor: null,
    instanceId: '39911',
    worldId: 'wrld_4b34',
    worldName: 'The Black Cat',
    person: { id: 'usr_' + id, displayName: 'Person ' + id, trustRank: null, standing: 'Ordinary', priorActions: 0, flags: [] },
    flagged: false,
    reason: null,
    byThisDevice: false,
    data: null,
  }
}

test('the socket comes first, and its events arrive with the cursor remembered', async () => {
  const h = harness()

  h.stream.start()
  assert.equal(h.stream.getState(), 'connecting')
  await h.settle()

  const socket = h.latest()
  assert.ok(socket.address.startsWith('wss://modbot.example/api/live/ws?ticket=t-0'))
  assert.ok(!socket.address.includes('after='), 'the first connection starts from now')

  socket.open()
  assert.equal(h.stream.getState(), 'live')

  socket.deliver({ kind: 'hello', version: 1, heartbeatSeconds: 30, cursor: '10' })
  socket.deliver({ kind: 'event', event: event('11') })
  socket.deliver({ kind: 'heartbeat', cursor: '14' })

  assert.deepEqual(h.events.map((e) => e.id), ['11'])
  assert.equal(h.stream.getCursor(), '14')
  assert.equal(h.polls.length, 0)
})

test('a dropped socket is reopened from the last cursor after a backoff', async () => {
  const h = harness()
  h.stream.start()
  await h.settle()

  const first = h.latest()
  first.open()
  first.deliver({ kind: 'event', event: event('21') })

  first.drop()
  assert.equal(h.stream.getState(), 'connecting')
  assert.equal(h.sockets.length, 1)

  await h.advance(RULES.backoffMinMs - 1)
  assert.equal(h.sockets.length, 1, 'not straight away')

  await h.advance(1)
  assert.equal(h.sockets.length, 2)
  assert.ok(h.latest().address.includes('after=21'))
})

test('three failures within two minutes mean polling for a while, then the socket again', async () => {
  const h = harness()
  h.stream.start()
  await h.settle()

  for (let attempt = 0; attempt < RULES.dropsBeforePolling; attempt++) {
    h.latest().drop()
    await h.advance(RULES.backoffMaxMs)
  }

  assert.equal(h.stream.getState(), 'polling')
  assert.equal(h.sockets.length, RULES.dropsBeforePolling)
  assert.equal(h.polls.length, 1)
  assert.equal(h.polls[0].wait, RULES.pollWaitSeconds)

  // Events by polling, with the cursor carried on to the next poll, which goes at once.
  await h.answerPoll({ events: [event('31'), event('32')], cursor: '32', more: false })

  assert.deepEqual(h.events.map((e) => e.id), ['31', '32'])
  assert.equal(h.polls.length, 2)
  assert.equal(h.polls[1].after, '32')
  assert.equal(h.stream.getState(), 'polling')

  // The spell ends, and the socket is tried again from where polling got to.
  const before = h.sockets.length
  await h.advance(RULES.pollingSpellMs)
  await h.answerPoll({ events: [], cursor: '32', more: false })

  assert.equal(h.stream.getState(), 'connecting')
  assert.equal(h.sockets.length, before + 1)
  assert.ok(h.latest().address.includes('after=32'))
})

test('a ticket that cannot be had counts as a failed connect', async () => {
  const h = harness({ ticketFails: true })
  h.stream.start()
  await h.settle()

  assert.equal(h.sockets.length, 0)
  assert.equal(h.stream.getState(), 'connecting')

  await h.advance(RULES.backoffMaxMs)
  await h.advance(RULES.backoffMaxMs)

  assert.equal(h.stream.getState(), 'polling')
})

test('a failed poll backs off; a quiet poll does not', async () => {
  const h = harness({ ticketFails: true })
  h.stream.start()
  await h.settle()
  await h.advance(RULES.backoffMaxMs)
  await h.advance(RULES.backoffMaxMs)
  assert.equal(h.stream.getState(), 'polling')
  const first = h.polls.length

  await h.failPoll()
  assert.equal(h.polls.length, first, 'waiting out the failure')

  await h.advance(RULES.backoffMinMs)
  assert.equal(h.polls.length, first + 1)

  // Quiet: the next poll goes at once.
  await h.answerPoll({ events: [], cursor: '40', more: false })
  assert.equal(h.polls.length, first + 2)
  assert.equal(h.polls[h.polls.length - 1].after, '40')
})

test('access removed stops the stream for good', async () => {
  const h = harness()
  h.stream.start()
  await h.settle()
  h.latest().open()

  h.latest().drop(4003)

  assert.equal(h.stream.getState(), 'stopped')
  await h.advance(RULES.backoffMaxMs * 2)
  assert.equal(h.sockets.length, 1)

  h.stream.start()
  assert.equal(h.sockets.length, 1)
})

test('stopping closes the socket and forgets nothing about where it was', async () => {
  const h = harness()
  h.stream.start()
  await h.settle()
  const socket = h.latest()
  socket.open()
  socket.deliver({ kind: 'event', event: event('51') })

  h.stream.stop()
  assert.equal(h.stream.getState(), 'off')
  assert.ok(socket.closed)

  h.stream.start()
  await h.settle()
  assert.ok(h.latest().address.includes('after=51'))
  assert.equal(h.stream.getState(), 'connecting')
})

test('a ticket refused for good stops the stream rather than asking forever', async () => {
  const h = harness({ ticketRefused: true })
  h.stream.start()
  await h.settle()

  assert.equal(h.stream.getState(), 'stopped')
  await h.advance(RULES.backoffMaxMs * 4)
  assert.equal(h.sockets.length, 0)
  assert.equal(h.polls.length, 0)
})

test('the state has a word for the screen', () => {
  assert.equal(stateWord('live'), 'Live')
  assert.equal(stateWord('polling'), 'Polling')
  assert.equal(stateWord('connecting'), 'Connecting')
  assert.equal(stateWord('stopped'), 'Stopped')
  assert.equal(stateWord('off'), 'Off')
})
