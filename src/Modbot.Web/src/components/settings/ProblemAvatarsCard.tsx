import { useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, type ProblemAvatar } from '@/lib/api'
import { Outcome } from './fields'
import { failure } from './api/shared'
import { SettingsCard } from './SettingsCard'

/** The fewest characters typed before names Modbot has seen are suggested. */
const SUGGEST_FROM = 2

/**
 * Settings → Moderation: the avatars that raise a flagged alert on whoever wears one.
 *
 * An avatar is added here by its exact name, by pasting its vrchat.com link or `avtr_` id, or from
 * a person's popup (Mark avatar). While a name is typed, avatars Modbot has seen people wear are
 * offered. The server matches by name, because VRChat's log never gives an avatar id.
 */
export function ProblemAvatarsCard() {
  const [avatars, setAvatars] = useState<ProblemAvatar[] | null>(null)
  const [busyId, setBusyId] = useState('')
  const [text, setText] = useState('')
  const [adding, setAdding] = useState(false)
  const [suggestions, setSuggestions] = useState<string[]>([])
  const [error, setError] = useState('')

  useEffect(() => {
    api
      .problemAvatars()
      .then(setAvatars)
      .catch((e: unknown) => setError(failure(e, 'Could not load the avatars.')))
  }, [])

  useEffect(() => {
    const typed = text.trim()
    if (typed.length < SUGGEST_FROM) {
      setSuggestions([])
      return
    }

    // Wait for a pause in typing, and drop an answer that a later keystroke has already outdated.
    let current = true
    const timer = setTimeout(() => {
      api
        .suggestProblemAvatars(typed)
        .then((names) => current && setSuggestions(names))
        .catch(() => current && setSuggestions([]))
    }, 250)

    return () => {
      current = false
      clearTimeout(timer)
    }
  }, [text, avatars])

  const add = (value: string) => {
    if (!value.trim()) return

    setAdding(true)
    setError('')

    api
      .addProblemAvatar(value)
      .then(() => api.problemAvatars())
      .then((list) => {
        setAvatars(list)
        setText('')
        setSuggestions([])
      })
      .catch((e: unknown) => setError(failure(e, 'Could not add it.')))
      .finally(() => setAdding(false))
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    add(text)
  }

  const remove = (id: string) => {
    setBusyId(id)
    setError('')

    api
      .removeProblemAvatar(id)
      .then(() => setAvatars((current) => current?.filter((a) => a.id !== id) ?? null))
      .catch((e: unknown) => setError(failure(e, 'Could not remove it.')))
      .finally(() => setBusyId(''))
  }

  return (
    <SettingsCard title="Problem avatars" span={12} footer={error ? <Outcome tone="problem">{error}</Outcome> : undefined}>
      <form className="mb-3 flex flex-col gap-2" onSubmit={submit}>
        <div className="flex items-center gap-2">
          <Input
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="Avatar name or link"
            aria-label="Avatar name or link"
            autoComplete="off"
            disabled={adding}
          />
          <Button type="submit" size="xs" disabled={adding || !text.trim()}>
            Add
          </Button>
        </div>
        {suggestions.length > 0 && (
          <ul className="flex flex-col gap-1">
            {suggestions.map((name) => (
              <li key={name}>
                <Button
                  type="button"
                  variant="outline"
                  size="xs"
                  className="max-w-full justify-start whitespace-normal break-words text-left"
                  disabled={adding}
                  onClick={() => add(name)}
                >
                  {name}
                </Button>
              </li>
            ))}
          </ul>
        )}
      </form>
      {avatars?.length === 0 && <p className="text-muted-foreground">None</p>}
      {avatars && avatars.length > 0 && (
        <ul className="flex flex-col gap-1">
          {avatars.map((a) => (
            <li key={a.id} className="flex items-center justify-between gap-3">
              <span className="min-w-0 break-words">
                {a.name || a.avatarId}
                <span className="ms-2 text-muted-foreground">{a.avatarId ? 'Linked' : 'By name'}</span>
              </span>
              <Button variant="outline" size="xs" disabled={busyId === a.id} onClick={() => remove(a.id)}>
                Remove
              </Button>
            </li>
          ))}
        </ul>
      )}
    </SettingsCard>
  )
}
