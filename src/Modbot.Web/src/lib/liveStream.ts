/**
 * Live updates: the WebSocket first, long polling when the socket cannot be kept, and the socket
 * again on a schedule (live updates design §6).
 *
 * Pure: everything that touches the browser -- the ticket request, the socket, the poll request,
 * timers -- is handed in, so the rules can be tested in Node. `useLiveStream` wires it to the
 * real ones.
 *
 * The rules:
 * - Open the socket. Every event and heartbeat carries a cursor; a reconnect, by either route,
 *   asks for events after it, so a dropped connection costs delay and never an event.
 * - When the socket cannot connect, or drops, `dropsBeforePolling` times within `dropWindowMs`,
 *   poll for `pollingSpellMs`, then try the socket again.
 * - Between attempts, back off: doubling from `backoffMinMs` up to `backoffMaxMs`, with jitter.
 * - A quiet poll is the ordinary answer and backs nothing off: the next one goes at once.
 * - `4003` (access removed) stops the stream for good; the page has to be reloaded.
 */

export type LivePerson = {
  id: string
  displayName: string | null
  /** VRChat's trust rank when Modbot knows it. Null until the trust rank sync lands. */
  trustRank: string | null
  standing: string
  priorActions: number
  flags: string[]
}

export type LiveSubject = { platform: string; id: string; kind: string }

export type LiveActor = { platform: string; id: string; name: string | null }

/**
 * One event: a fact under another name. `kind` names the few a screen does something particular
 * with (`person_joined`, `flagged_join`, `alert`, ...) and is `fact` for everything else; `type`
 * is the fact's own type, which is what most screens match on.
 */
export type LiveEvent = {
  id: string
  cursor: string
  kind: string
  type: string
  typeRaw: string | null
  category: string
  label: string
  source: string
  at: string
  occurredBefore: string | null
  observedAt: string
  subject: LiveSubject
  actor: LiveActor | null
  instanceId: string | null
  worldId: string | null
  person: LivePerson | null
  flagged: boolean
  reason: string | null
  byThisDevice: boolean
  data: unknown
}

export type LivePollPage = { events: LiveEvent[]; cursor: string; more: boolean }

export type LiveState = 'off' | 'connecting' | 'live' | 'polling' | 'stopped'

/** The kinds the Live page redraws for. */
export const PRESENCE_KINDS = new Set(['person_joined', 'flagged_join', 'person_left', 'person_here', 'watch_stopped'])

export const ROOM_KINDS = new Set(['room_opened', 'room_closed', 'room_changed'])

export const REVIEW_KINDS = new Set(['review_opened', 'review_closed'])

/** The part of a WebSocket the stream uses, so a test can hand in a fake. */
export type SocketLike = {
  send(data: string): void
  close(code?: number): void
  onopen: (() => void) | null
  onmessage: ((message: { data: unknown }) => void) | null
  onclose: ((event: { code: number }) => void) | null
  onerror: (() => void) | null
}

export type LiveStreamDeps = {
  /** A one-use ticket for the socket, from POST /api/live/tickets. */
  ticket: () => Promise<string>
  /** Opens a socket to the address. */
  openSocket: (address: string) => SocketLike
  /** The socket's address without the query. */
  socketAddress: () => string
  /** One long poll: events after `after`, waiting up to `waitSeconds`. */
  poll: (after: string | null, waitSeconds: number) => Promise<LivePollPage>
  now: () => number
  setTimer: (run: () => void, ms: number) => unknown
  clearTimer: (handle: unknown) => void
  /** 0..1, for jitter. */
  random?: () => number
}

export const RULES = {
  dropsBeforePolling: 3,
  dropWindowMs: 120_000,
  pollingSpellMs: 300_000,
  pollWaitSeconds: 25,
  backoffMinMs: 1_000,
  backoffMaxMs: 30_000,
}

/** Close codes after which reconnecting cannot help. */
const FATAL_CLOSE_CODES = new Set([4003])

/**
 * A refusal that trying again cannot fix: the ticket or the poll was answered 401 or 403. The
 * hook marks those; the stream stops rather than asking every half minute for good.
 */
export function isFatal(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { fatal?: boolean }).fatal === true
}

export type LiveStream = {
  start(): void
  stop(): void
  getState(): LiveState
  getCursor(): string | null
  onState(listener: (state: LiveState) => void): () => void
  onEvent(listener: (event: LiveEvent) => void): () => void
}

export function createLiveStream(deps: LiveStreamDeps): LiveStream {
  const random = deps.random ?? Math.random

  let state: LiveState = 'off'
  let cursor: string | null = null
  let running = false
  let generation = 0
  let socket: SocketLike | null = null
  let timer: unknown = null
  let failures = 0
  let drops: number[] = []
  let pollUntil: number | null = null

  const stateListeners = new Set<(state: LiveState) => void>()
  const eventListeners = new Set<(event: LiveEvent) => void>()

  function setState(next: LiveState) {
    if (state === next) return
    state = next
    for (const listener of stateListeners) listener(state)
  }

  function emit(event: LiveEvent) {
    cursor = event.cursor
    for (const listener of eventListeners) listener(event)
  }

  function clearTimer() {
    if (timer !== null) {
      deps.clearTimer(timer)
      timer = null
    }
  }

  function later(run: () => void, ms: number) {
    clearTimer()
    const mine = generation
    timer = deps.setTimer(() => {
      timer = null
      if (running && mine === generation) run()
    }, ms)
  }

  function backoffMs(): number {
    const base = Math.min(RULES.backoffMaxMs, RULES.backoffMinMs * 2 ** Math.max(0, failures - 1))
    return Math.round(base * (0.5 + 0.5 * random()))
  }

  function closeSocket() {
    if (!socket) return
    const closing = socket
    socket = null
    closing.onopen = null
    closing.onmessage = null
    closing.onclose = null
    closing.onerror = null
    try {
      closing.close(1000)
    } catch {
      // Already gone.
    }
  }

  /** A failed connect or a drop. Polls when they pile up; otherwise tries again after a backoff. */
  function dropped() {
    const now = deps.now()
    drops.push(now)
    drops = drops.filter((at) => now - at <= RULES.dropWindowMs)

    if (drops.length >= RULES.dropsBeforePolling) {
      drops = []
      failures = 0
      pollUntil = now + RULES.pollingSpellMs
      pollLoop()
      return
    }

    failures++
    setState('connecting')
    later(connectSocket, backoffMs())
  }

  function connectSocket() {
    if (!running) return
    const mine = ++generation
    setState('connecting')

    deps.ticket().then(
      (ticket) => {
        if (!running || mine !== generation) return

        const address = `${deps.socketAddress()}?ticket=${encodeURIComponent(ticket)}${cursor ? `&after=${encodeURIComponent(cursor)}` : ''}`
        const opened = deps.openSocket(address)
        socket = opened

        opened.onopen = () => {
          if (socket !== opened) return
          failures = 0
          setState('live')
        }

        opened.onmessage = (message) => {
          if (socket !== opened) return
          let parsed: { kind?: string; event?: LiveEvent; cursor?: string }
          try {
            parsed = JSON.parse(String(message.data))
          } catch {
            return
          }

          if (parsed.kind === 'event' && parsed.event) emit(parsed.event)
          else if ((parsed.kind === 'hello' || parsed.kind === 'heartbeat') && parsed.cursor) cursor = parsed.cursor
        }

        opened.onerror = () => {
          // The close that follows carries the outcome.
        }

        opened.onclose = (event) => {
          if (socket !== opened) return
          socket = null
          if (!running) return

          if (FATAL_CLOSE_CODES.has(event.code)) {
            stopForGood()
            return
          }

          dropped()
        }
      },
      (error: unknown) => {
        if (!running || mine !== generation) return
        if (isFatal(error)) stopForGood()
        else dropped()
      },
    )
  }

  function stopForGood() {
    running = false
    generation++
    clearTimer()
    closeSocket()
    setState('stopped')
  }

  function pollLoop() {
    if (!running) return
    const mine = ++generation
    setState('polling')

    deps.poll(cursor, RULES.pollWaitSeconds).then(
      (page) => {
        if (!running || mine !== generation) return
        failures = 0
        for (const event of page.events) emit(event)
        cursor = page.cursor
        next()
      },
      (error: unknown) => {
        if (!running || mine !== generation) return
        if (isFatal(error)) {
          stopForGood()
          return
        }

        failures++
        later(next, backoffMs())
      },
    )

    function next() {
      if (pollUntil !== null && deps.now() >= pollUntil) {
        pollUntil = null
        connectSocket()
        return
      }

      pollLoop()
    }
  }

  return {
    start() {
      if (running || state === 'stopped') return
      running = true
      connectSocket()
    },

    stop() {
      if (!running) return
      running = false
      generation++
      clearTimer()
      closeSocket()
      pollUntil = null
      drops = []
      failures = 0
      setState('off')
    },

    getState: () => state,
    getCursor: () => cursor,

    onState(listener) {
      stateListeners.add(listener)
      return () => {
        stateListeners.delete(listener)
      }
    },

    onEvent(listener) {
      eventListeners.add(listener)
      return () => {
        eventListeners.delete(listener)
      }
    },
  }
}

/** The state in one word, for a screen. */
export function stateWord(state: LiveState): string {
  switch (state) {
    case 'live':
      return 'Live'
    case 'polling':
      return 'Polling'
    case 'connecting':
      return 'Connecting'
    case 'stopped':
      return 'Stopped'
    default:
      return 'Off'
  }
}
