import { useEffect, useRef } from 'react'
import { autoLinkOf, cleanFillInName, fillInMarker, splitFillIns, withoutLinks } from '@/lib/calendarFillIns'
import { cn } from '@/lib/utils'

const CHIP_ATTRIBUTE = 'data-fill-in'

const CHIP_CLASS =
  'mx-0.5 inline-block select-none rounded-sm border border-(length:--hairline) border-input px-1.5 align-baseline data-[picked]:ring-2 data-[picked]:ring-ring'

/** The box clicked on, drawn picked until the caret moves on. */
const PICKED = 'data-picked'

// A fill-in is asked for when a template is used; an automatic link is filled by Modbot, and looks different.
const FILL_IN_TONE = 'bg-accent text-accent-foreground'
const LINK_TONE = 'bg-muted text-foreground'

function makeChip(name: string): HTMLElement {
  const chip = document.createElement('span')
  chip.setAttribute(CHIP_ATTRIBUTE, name)
  chip.contentEditable = 'false'
  chip.className = `${CHIP_CLASS} ${autoLinkOf(name) ? LINK_TONE : FILL_IN_TONE}`
  chip.style.fontSize = 'var(--text-small)'
  chip.textContent = name
  return chip
}

/**
 * Draws the text into the box: each blank as a box carrying its name, each line break as a break.
 * Where links are not allowed (a title), a link's name is drawn as plain words.
 */
function draw(box: HTMLElement, text: string, links: boolean) {
  box.replaceChildren()

  for (const piece of splitFillIns(text)) {
    if (piece.kind === 'link' && !links) {
      box.append(document.createTextNode(piece.name))
      continue
    }

    if (piece.kind !== 'text') {
      box.append(makeChip(piece.name))
      continue
    }

    piece.text.split('\n').forEach((line, index) => {
      if (index > 0) box.append(document.createElement('br'))
      if (line !== '') box.append(document.createTextNode(line))
    })
  }

  // A browser shows a last line break only when something follows it; the reader drops this one.
  if (box.lastChild?.nodeName === 'BR') box.append(document.createElement('br'))
}

function read(node: Node, out: string[]) {
  node.childNodes.forEach((child) => {
    if (child.nodeType === Node.TEXT_NODE) {
      out.push((child.textContent ?? '').replace(/ /g, ' '))
    } else if (child instanceof HTMLElement) {
      if (child.hasAttribute(CHIP_ATTRIBUTE)) {
        out.push(fillInMarker(child.getAttribute(CHIP_ATTRIBUTE) ?? ''))
      } else if (child.nodeName === 'BR') {
        out.push('\n')
      } else {
        // A block a browser wrapped a line in: it starts a new line.
        if (out.length > 0 && !out[out.length - 1].endsWith('\n')) out.push('\n')
        read(child, out)
      }
    }
  })
}

/** The text the box holds, its blanks back in their stored form. */
function text(box: HTMLElement): string {
  const out: string[] = []
  read(box, out)
  const joined = out.join('')
  return box.lastChild?.nodeName === 'BR' ? joined.slice(0, -1) : joined
}

/**
 * A title or description in the template editor, where a blank shows as a box carrying its name and
 * never as its stored code. Typed words and boxes sit in one line, and a box deletes like a letter.
 *
 * `onActive` hands the parent a function that puts a new box at the caret (or at the end when the
 * caret has not been in this field), called whenever the field is clicked into.
 *
 * `links` is false for a title: it draws no link boxes, takes none from the Links row (that function
 * answers false), and a link name typed or pasted in stays plain words.
 */
export function FillInText({
  value,
  onChange,
  label,
  multiline = false,
  links = true,
  onActive,
  onPutText,
  putFirst = false,
  placeholder = '',
  className,
}: {
  value: string
  onChange: (value: string) => void
  label: string
  multiline?: boolean
  links?: boolean
  onActive?: (insert: (name: string) => boolean) => void
  /** Like `onActive`, for a function that puts plain words (an emoji) at the caret. */
  onPutText?: (put: (words: string) => boolean) => void
  /** Hands `onPutText` its function when the box appears too, so the parent has one before any click. */
  putFirst?: boolean
  /** Muted words drawn in the box while it is empty. */
  placeholder?: string
  /** Classes laid over the box's own, to sit it inside something else (a service's card). */
  className?: string
}) {
  const box = useRef<HTMLDivElement>(null)
  // Where the caret was last, because pressing a button elsewhere takes it away.
  const caret = useRef<Range | null>(null)

  useEffect(() => {
    if (box.current && text(box.current) !== value) {
      draw(box.current, value, links)
      caret.current = null
    }
  }, [value, links])

  const remember = () => {
    const selection = window.getSelection()
    if (box.current && selection && selection.rangeCount > 0 && box.current.contains(selection.anchorNode)) {
      caret.current = selection.getRangeAt(0).cloneRange()
    }
  }

  const unpick = () => box.current?.querySelectorAll(`[${PICKED}]`).forEach((chip) => chip.removeAttribute(PICKED))

  // A box clicked on is picked whole: the arrow keys then put the caret before or after it, and
  // Backspace or Delete takes it out.
  const pick = (target: EventTarget) => {
    unpick()
    const chip = target instanceof HTMLElement ? target.closest(`[${CHIP_ATTRIBUTE}]`) : null
    if (!chip || !box.current?.contains(chip)) return
    chip.setAttribute(PICKED, '')
    const range = document.createRange()
    range.selectNode(chip)
    const selection = window.getSelection()
    selection?.removeAllRanges()
    selection?.addRange(range)
  }

  /** Puts `node` at the caret (or at the end when the caret has not been in this field) and moves the caret after it. */
  const place = (field: HTMLElement, node: Node) => {
    field.focus()

    const range = caret.current ?? document.createRange()
    if (!caret.current) {
      range.selectNodeContents(field)
      range.collapse(false)
    }

    range.deleteContents()
    range.insertNode(node)
    range.setStartAfter(node)
    range.collapse(true)

    const selection = window.getSelection()
    selection?.removeAllRanges()
    selection?.addRange(range)
    caret.current = range.cloneRange()

    onChange(text(field))
  }

  /** False when the field is gone from the screen, so nothing was put in. */
  const insert = (name: string) => {
    const field = box.current
    const clean = cleanFillInName(name)
    if (!field) return false
    if (!links && autoLinkOf(clean)) return false
    if (clean === '') return true

    place(field, makeChip(clean))
    return true
  }

  /** Puts plain words (an emoji) at the caret. False when the field is gone from the screen. */
  const putText = (words: string) => {
    const field = box.current
    if (!field) return false
    if (words !== '') place(field, document.createTextNode(words))
    return true
  }

  // The parent has this box's function from the start, before anything was clicked.
  useEffect(() => {
    if (putFirst) onPutText?.(putText)
    // Once, when the box appears.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <div
      ref={box}
      role="textbox"
      aria-label={label}
      aria-multiline={multiline}
      contentEditable
      suppressContentEditableWarning
      spellCheck
      data-placeholder={placeholder || undefined}
      className={cn(
        placeholder !== '' && 'empty:before:pointer-events-none empty:before:text-muted-foreground empty:before:content-[attr(data-placeholder)]',
        'block w-full min-w-0 rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 py-1 text-base outline-none selection:bg-primary selection:text-primary-foreground md:text-(length:--text-base)',
        'focus-visible:border-ring focus-visible:ring-1 focus-visible:ring-ring',
        multiline ? 'min-h-[calc(var(--control-h)*2.4)] whitespace-pre-wrap [overflow-wrap:anywhere]' : 'h-(--control-h) overflow-hidden whitespace-nowrap',
        className,
      )}
      onFocus={() => {
        onActive?.(insert)
        onPutText?.(putText)
      }}
      onClick={(e) => {
        pick(e.target)
        remember()
        onActive?.(insert)
        onPutText?.(putText)
      }}
      onKeyUp={remember}
      onBlur={() => {
        unpick()
        remember()
      }}
      onInput={(e) => {
        const typed = text(e.currentTarget)
        const kept = links ? typed : withoutLinks(typed)

        if (kept !== typed) {
          // A link name typed or pasted into a title: the brackets go, the words stay.
          draw(e.currentTarget, kept, links)
          const selection = window.getSelection()
          const end = document.createRange()
          end.selectNodeContents(e.currentTarget)
          end.collapse(false)
          selection?.removeAllRanges()
          selection?.addRange(end)
          caret.current = null
        }

        // A browser leaves a line break in a box emptied by deleting; the box must be truly empty to show its placeholder.
        if (kept === '') e.currentTarget.replaceChildren()
        onChange(kept)
      }}
      onKeyDown={(e) => {
        const picked = e.currentTarget.querySelector(`[${PICKED}]`)
        if (picked) {
          unpick()
          if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
            e.preventDefault()
            const range = document.createRange()
            if (e.key === 'ArrowLeft') range.setStartBefore(picked)
            else range.setStartAfter(picked)
            range.collapse(true)
            const selection = window.getSelection()
            selection?.removeAllRanges()
            selection?.addRange(range)
            return
          }
          if (e.key === 'Backspace' || e.key === 'Delete') {
            e.preventDefault()
            picked.remove()
            onChange(text(e.currentTarget))
            return
          }
        }
        if (e.key !== 'Enter') return
        e.preventDefault()
        if (multiline) document.execCommand('insertLineBreak')
      }}
      onPaste={(e) => {
        e.preventDefault()
        const pasted = e.clipboardData.getData('text/plain')
        document.execCommand('insertText', false, multiline ? pasted : pasted.replace(/\s*[\r\n]+\s*/g, ' '))
      }}
      onDrop={(e) => {
        // Dropped styled text would bring its own elements, which read() turns into line breaks.
        e.preventDefault()
        const dropped = e.dataTransfer.getData('text/plain')
        if (dropped === '') return

        const at = document.caretRangeFromPoint?.(e.clientX, e.clientY)
        if (at && e.currentTarget.contains(at.startContainer)) {
          const selection = window.getSelection()
          selection?.removeAllRanges()
          selection?.addRange(at)
        }
        document.execCommand('insertText', false, multiline ? dropped : dropped.replace(/\s*[\r\n]+\s*/g, ' '))
      }}
    />
  )
}

/**
 * A text read on the page rather than edited: its blanks and automatic links as boxes carrying their
 * names, never as their stored codes.
 */
export function FillInShown({ text }: { text: string }) {
  return splitFillIns(text).map((piece, i) =>
    piece.kind === 'text' ? (
      piece.text
    ) : (
      <span key={i} className={cn(CHIP_CLASS, piece.kind === 'link' ? LINK_TONE : FILL_IN_TONE)}>
        {piece.name}
      </span>
    ),
  )
}
