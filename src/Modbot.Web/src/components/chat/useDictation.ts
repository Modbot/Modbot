import { useCallback, useEffect, useRef, useState } from 'react'

/** The parts of the browser's speech recognition this page uses. Chrome and Edge have it; Firefox does not. */
interface Recognition {
  continuous: boolean
  interimResults: boolean
  lang: string
  onresult: ((e: RecognitionResultEvent) => void) | null
  onerror: ((e: { error: string }) => void) | null
  onend: (() => void) | null
  start: () => void
  stop: () => void
  abort: () => void
}

interface RecognitionResultEvent {
  results: ArrayLike<ArrayLike<{ transcript: string }> & { isFinal: boolean }>
}

type RecognitionConstructor = new () => Recognition

function recognitionConstructor(): RecognitionConstructor | null {
  if (typeof window === 'undefined') return null
  const w = window as unknown as Record<string, RecognitionConstructor | undefined>
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null
}

/** The text already in the box, with a space before what is said next, unless it already ends in one. */
export function dictationStart(text: string): string {
  return text.length === 0 || /\s$/.test(text) ? text : `${text} `
}

/** What the box shows while dictating: the text from before, then what was said (finished and still being heard). */
export function dictationText(
  start: string,
  results: ArrayLike<ArrayLike<{ transcript: string }>>,
  maxLength: number,
): string {
  let said = ''
  for (let i = 0; i < results.length; i++) said += results[i][0]?.transcript ?? ''
  return (start + said.trimStart()).slice(0, maxLength)
}

/**
 * Dictation into a text box: tap to start, tap again to stop. What is said fills the box live, after
 * the text already there, and is never sent. `supported` is false where the browser has no speech
 * recognition, and the page shows no button then.
 */
export function useDictation(text: string, setText: (next: string) => void, maxLength: number) {
  const supported = recognitionConstructor() !== null
  const [listening, setListening] = useState(false)
  const [blocked, setBlocked] = useState(false)
  const recognition = useRef<Recognition | null>(null)
  const latest = useRef({ text, setText })
  useEffect(() => {
    latest.current = { text, setText }
  })

  /** Ends dictation at once, dropping anything not yet heard. */
  const cancel = useCallback(() => {
    const running = recognition.current
    recognition.current = null
    if (running) {
      running.onresult = null
      running.onerror = null
      running.onend = null
      running.abort()
    }
    setListening(false)
  }, [])

  const toggle = useCallback(() => {
    if (recognition.current) {
      recognition.current.stop()
      return
    }

    const Recognition = recognitionConstructor()
    if (!Recognition) return

    const run = new Recognition()
    const start = dictationStart(latest.current.text)
    run.continuous = true
    run.interimResults = true
    run.lang = navigator.language
    run.onresult = (e) => latest.current.setText(dictationText(start, e.results, maxLength))
    run.onerror = (e) => {
      if (e.error === 'not-allowed' || e.error === 'service-not-allowed') setBlocked(true)
    }
    run.onend = () => {
      if (recognition.current === run) recognition.current = null
      setListening(false)
    }

    setBlocked(false)
    recognition.current = run
    try {
      run.start()
      setListening(true)
    } catch {
      recognition.current = null
    }
  }, [maxLength])

  useEffect(() => cancel, [cancel])

  return { supported, listening, blocked, toggle, cancel }
}
