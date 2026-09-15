import { StrictMode } from 'react'
import { createRoot, hydrateRoot } from 'react-dom/client'
import App from './App'
import './index.css'

const root = document.getElementById('root')!

// Set by scripts/prerender.ts when the privacy policy was built, so the hydrated footer matches the
// rendered one.
const app = (
  <StrictMode>
    <App privacy={root.dataset.privacy === 'yes'} />
  </StrictMode>
)

// The built page arrives already rendered; the dev server's does not.
if (root.firstElementChild) hydrateRoot(root, app)
else createRoot(root).render(app)
