import { useEffect, useRef, useSyncExternalStore } from 'react'
import { api, ApiError } from '@/lib/api'
import {
  createLiveStream,
  type LiveEvent,
  type LivePollPage,
  type LiveState,
  type LiveStream,
  type SocketLike,
} from '@/lib/liveStream'

/** A 401 or 403 is not going to change by asking again: the stream stops rather than retrying. */
function refusedForGood(error: unknown): never {
  if (error instanceof ApiError && (error.status === 401 || error.status === 403))
    throw Object.assign(new Error(error.message), { fatal: true })

  throw error
}

/**
 * One live stream for the whole page, however many components read it.
 *
 * The Live page, the alerts card and the sidebar's review count all want the same connection,
 * and three sockets would cost the server three connections for one person. The stream runs
 * while any component is subscribed and while the tab is on screen: a tab left open in the
 * background holds nothing open, and comes back with its cursor when it is looked at again.
 */

let stream: LiveStream | null = null
let subscribers = 0
let watchingVisibility = false

function theStream(): LiveStream {
  stream ??= createLiveStream({
    ticket: () => api.liveTicket().then((t) => t.ticket, refusedForGood),
    openSocket: (address) => {
      const socket = new WebSocket(address)
      const like: SocketLike = {
        send: (data) => socket.send(data),
        close: (code) => socket.close(code),
        onopen: null,
        onmessage: null,
        onclose: null,
        onerror: null,
      }

      socket.onopen = () => like.onopen?.()
      socket.onmessage = (message) => like.onmessage?.({ data: message.data })
      socket.onclose = (event) => like.onclose?.({ code: event.code })
      socket.onerror = () => like.onerror?.()

      return like
    },
    socketAddress: () => `${window.location.protocol === 'https:' ? 'wss' : 'ws'}://${window.location.host}/api/live/ws`,
    poll: (after, waitSeconds) =>
      api.livePoll(after, waitSeconds).then((page) => page as LivePollPage, refusedForGood),
    now: () => performance.now(),
    setTimer: (run, ms) => window.setTimeout(run, ms),
    clearTimer: (handle) => window.clearTimeout(handle as number),
  })

  return stream
}

function follow() {
  const live = theStream()
  if (subscribers > 0 && document.visibilityState === 'visible') live.start()
  else live.stop()
}

function subscribe(listener: () => void): () => void {
  const live = theStream()
  const off = live.onState(listener)
  subscribers++

  if (!watchingVisibility) {
    watchingVisibility = true
    document.addEventListener('visibilitychange', follow)
  }

  follow()

  return () => {
    off()
    subscribers--
    follow()
  }
}

/**
 * The stream's state, and each event as it arrives.
 *
 * `onEvent` may change between renders; the latest one is called, without resubscribing.
 */
export function useLiveStream(onEvent?: (event: LiveEvent) => void): LiveState {
  const state = useSyncExternalStore(subscribe, () => theStream().getState(), () => 'off' as LiveState)
  const latest = useRef(onEvent)

  useEffect(() => {
    latest.current = onEvent
  }, [onEvent])

  useEffect(() => theStream().onEvent((event) => latest.current?.(event)), [])

  return state
}
