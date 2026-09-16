import { createContext, useContext } from 'react'

/**
 * Whether this deployment is a public demo.
 *
 * A demo talks to nothing outside itself, so every screen that reports on VRChat is reporting on
 * something that was never going to answer. Left alone, the pages say so accurately and read as
 * faults: "not read yet", "couldn't refresh". They are not faults on a demo, and a first-time
 * visitor has no way to tell. The flag is here so those few places can say what they are instead.
 *
 * False everywhere but a demo, which is every real deployment.
 */
export const DemoContext = createContext(false)

export function useDemo(): boolean {
  return useContext(DemoContext)
}
