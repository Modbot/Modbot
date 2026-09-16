# Modbot.Web

The web app: everything your staff see in a browser, built with React, TypeScript and Vite. It has
no server of its own — `Modbot.Host` builds it and serves the result as static files next to its API.

## Running it in development

```bash
npm ci
npm run dev
```

Opens on `http://localhost:5173` and proxies `/api` to `http://localhost:8080` (see
`vite.config.ts`), so a local `Modbot.Host` needs to be running for anything past the sign-in page
to work. Changes reload in the browser as you save.

## Building it

```bash
npm run build
```

Runs the TypeScript project build (`tsc -b`) and then Vite, writing straight into
`../Modbot.Host/wwwroot` — there is no separate deploy step or artifact for the web app; it is part
of the `Modbot.Host` build and the Docker image at the repository root.

## Checks

```bash
npx tsc -b        # typecheck
npm run lint      # oxlint
npm run build     # the real build, above
npm test          # node's own test runner, over tests/*.test.ts
```

Run all four before pushing a change to this project.

## Where things are

| Path | Contents |
|---|---|
| `src/pages/` | One file per page, `src/pages/analytics/` and `src/pages/setup/` for the pages grouped under Analytics and the setup wizard |
| `src/components/` | Shared components, grouped by the area that uses them (`subject/` is the popup that opens over a page for a person, world or instance) |
| `src/components/ui/` | The primitive components everything else is built from |
| `src/lib/` | The API client and other code with no UI of its own |
| `src/index.css` | The design tokens — colours, spacing and the three densities (dense, comfortable, VR), explained in the comment at the top of the file |
| `tests/` | Tests run with Node's own test runner, not a browser or a DOM shim |

## Credits

The Credits page lists every third-party package Modbot ships. `npm run credits` regenerates the
data it reads (`src/lib/credits.json`) from the real NuGet and npm packages a build uses — run it
again after adding, removing or upgrading a package, and commit the result. See
`scripts/credits.mjs`.
