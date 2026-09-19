import { wireThemeButton } from '@/lib/theme'

// The pages with nothing moving on them: self-host, about, license and the privacy policy. They are
// rendered once at build and shipped as they are, so the only script they carry is the header's
// light and dark button.
wireThemeButton()
