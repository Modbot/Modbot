import type { ReactNode } from 'react'
import { Link } from '@/components/Link'

export function Mark() {
  return (
    <div className="grid size-7 shrink-0 place-items-center rounded-md bg-primary text-sm font-semibold text-primary-foreground">
      M
    </div>
  )
}

/** The narrow centred column the public pages use. */
export function Shell({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-screen bg-background px-4 py-10">
      <div className="mx-auto flex w-full max-w-lg flex-col gap-4">
        <header className="flex justify-center pb-2">
          <Link href="/" className="flex items-center gap-2">
            <Mark />
            <span className="font-semibold tracking-tight">my.modbot.co</span>
          </Link>
        </header>
        {children}
      </div>
    </div>
  )
}
