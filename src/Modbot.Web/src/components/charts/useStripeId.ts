import { useId } from 'react'

/** A stripe pattern id unique to one chart, safe inside `url(#…)` (React's ids carry colons). */
export function useStripeId(): string {
  return `stripes-${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`
}
