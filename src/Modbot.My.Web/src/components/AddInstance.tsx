import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { normaliseInstanceUrl } from '@/lib/instanceUrl'

export function AddInstance({ label, onAdd }: { label: string; onAdd: (url: string) => void }) {
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const url = normaliseInstanceUrl(value)
    if (!url) {
      setError('Not an https address.')
      return
    }
    setError(null)
    setValue('')
    onAdd(url)
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-2 border-t bg-muted/40 px-6 py-4">
      <div className="flex gap-2">
        <Input
          type="text"
          inputMode="url"
          aria-label="Instance URL"
          placeholder="https://"
          value={value}
          aria-invalid={error ? true : undefined}
          onChange={(e) => setValue(e.target.value)}
        />
        <Button type="submit">{label}</Button>
      </div>
      {error && (
        <p role="alert" className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}
    </form>
  )
}
