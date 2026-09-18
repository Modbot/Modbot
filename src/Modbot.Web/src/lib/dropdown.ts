import { Children, isValidElement, type ReactNode } from 'react'

/**
 * The parts of a dropdown that can be worked out without a browser.
 *
 * The dropdown in `components/ui/select.tsx` keeps the native control's shape -- `<option>`
 * elements as children -- so everything a caller wrote stays where it was. That means the list
 * has to be read back out of those children, and the keyboard has to be moved by hand rather
 * than by the operating system. Both are plain functions here so they can be tested without
 * rendering anything.
 */

/** One line of a dropdown, read out of an `<option>`. */
export type Choice = {
  value: string
  label: string
  disabled: boolean
  /** The `<optgroup>` heading above it, or null when it sits on its own. */
  group: string | null
}

/** Which way a key moves the cursor. */
export type Move = 'up' | 'down' | 'first' | 'last'

/**
 * The plain text of a node.
 *
 * An option's label is often written in pieces -- `{t.label} ({t.type})` -- and the closed
 * control has to show it as one string.
 */
export function textOf(node: ReactNode): string {
  if (node === null || node === undefined || typeof node === 'boolean') return ''
  if (typeof node === 'string') return node
  if (typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map((child) => textOf(child as ReactNode)).join('')
  if (isValidElement(node)) return textOf((node.props as { children?: ReactNode }).children)
  return ''
}

/** Every option in the children, flattened, with the heading each one sits under. */
export function readChoices(children: ReactNode): Choice[] {
  const found: Choice[] = []
  collect(children, null, found)
  return found
}

function collect(node: ReactNode, group: string | null, found: Choice[]): void {
  Children.forEach(node, (child) => {
    if (!isValidElement(child)) return

    const props = child.props as {
      value?: unknown
      label?: string
      disabled?: boolean
      children?: ReactNode
    }

    if (child.type === 'option') {
      const label = textOf(props.children)
      found.push({
        // An option with no value of its own is its own text, the same as the native control.
        value: props.value === undefined ? label : String(props.value),
        label,
        disabled: props.disabled === true,
        group,
      })
      return
    }

    if (child.type === 'optgroup') {
      collect(props.children, props.label ?? null, found)
      return
    }

    // A fragment, or anything else wrapped around options: look inside it. Callers build these
    // lists with `{list.map(...)}` and conditions, so options arrive nested more often than not.
    collect(props.children, group, found)
  })
}

/**
 * Where an arrow, Home or End key leaves the cursor.
 *
 * Disabled options are stepped over, and the ends do not wrap: running off the bottom of the
 * list leaves the cursor on the last option rather than jumping back to the top, which is what
 * every other list in the app does.
 */
export function moveActive(choices: Choice[], from: number, move: Move): number {
  const step = move === 'up' || move === 'last' ? -1 : 1
  const start =
    move === 'first'
      ? 0
      : move === 'last'
        ? choices.length - 1
        : from < 0
          ? step > 0
            ? 0
            : choices.length - 1
          : from + step

  for (let at = start; at >= 0 && at < choices.length; at += step) {
    if (!choices[at].disabled) return at
  }

  return from
}

/**
 * Where typing letters leaves the cursor.
 *
 * Typing the same letter over and over walks through the options that start with it; typing
 * anything else narrows on the option the cursor is already on. Both wrap, so a letter always
 * finds its options wherever the cursor happens to be.
 */
export function matchTyped(choices: Choice[], typed: string, from: number): number {
  const text = typed.toLowerCase()
  if (!text || choices.length === 0) return from

  const sameLetter = [...text].every((letter) => letter === text[0])
  const needle = sameLetter ? text[0] : text
  const start = sameLetter ? from + 1 : from < 0 ? 0 : from

  for (let step = 0; step < choices.length; step++) {
    const at = (((start + step) % choices.length) + choices.length) % choices.length
    const choice = choices[at]
    if (!choice.disabled && choice.label.toLowerCase().startsWith(needle)) return at
  }

  return from
}
