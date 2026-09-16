import { StrictMode } from 'react'
import { createRoot, hydrateRoot } from 'react-dom/client'
import RoomsPage from './RoomsPage'
import './index.css'

const root = document.getElementById('root')!

// Set by scripts/prerender.ts when the privacy policy was built, so the hydrated footer matches the
// rendered one.
const page = (
  <StrictMode>
    <RoomsPage privacy={root.dataset.privacy === 'yes'} />
  </StrictMode>
)

// The built page arrives already rendered; the dev server's does not.
if (root.firstElementChild) hydrateRoot(root, page)
else createRoot(root).render(page)
