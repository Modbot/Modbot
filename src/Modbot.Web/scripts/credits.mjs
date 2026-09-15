/**
 * Writes src/lib/credits.json, the data behind the Credits page.
 *
 * Regenerate with one command, from src/Modbot.Web:
 *
 *     npm run credits
 *
 * That restores the .NET solution and then runs this file. `npm ci` must have been run first, since
 * the web app's packages are read from node_modules. Run it again whenever a package is added,
 * removed or upgraded, and commit the result alongside that change.
 *
 * Nothing on the page is typed in by hand except the short list of services below. Everything else
 * is read from what the build actually uses:
 *
 *  - NuGet packages come from each src project's obj/project.assets.json (every package restore
 *    resolved, direct or not) and the .nuspec of each package in the NuGet packages folder (licence
 *    and project link). Test projects are left out: they ship to nobody.
 *  - Web app packages come from a real Vite build that writes nothing to disk, asking the bundler
 *    which files from node_modules ended up in the output. That is the honest answer to "what do we
 *    ship", unlike the lock file's production closure, which also lists the CSS compiler, the
 *    bundler's native binaries for every platform, and other things that never reach a browser.
 *    Direct dependencies in package.json are added even if the bundler folded them away (radix-ui is
 *    a re-export of its parts), and `tailwindcss` stays because its base styles are in the CSS.
 *  - Build tools are the devDependencies plus any Vite plugin imported by vite.config.ts.
 *
 * Output is sorted and has no timestamps, so a diff shows exactly which packages changed.
 */
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repo = path.resolve(web, '../..')
const out = path.join(web, 'src/lib/credits.json')

if (process.argv.includes('--restore')) {
  execFileSync('dotnet', ['restore', path.join(repo, 'Modbot.slnx')], { stdio: 'inherit' })
}

const byName = (a, b) => {
  const x = a.name.toLowerCase()
  const y = b.name.toLowerCase()
  return x < y ? -1 : x > y ? 1 : 0
}

/** Turns the many ways a package records its link into one plain https address. */
function cleanUrl(raw) {
  if (!raw) return null
  let url = String(raw).trim().replaceAll('&amp;', '&')
  // "github:user/repo" and bare "user/repo", as npm allows.
  const shorthand = url.match(/^(?:github:)?([\w.-]+\/[\w.-]+)$/)
  if (shorthand) url = `https://github.com/${shorthand[1]}`
  url = url
    .replace(/^git\+/, '')
    .replace(/^git:\/\//, 'https://')
    .replace(/^ssh:\/\/git@/, 'https://')
    .replace(/^git@([^:]+):/, 'https://$1/')
    .replace(/^http:\/\//, 'https://')
  try {
    const parsed = new URL(url)
    for (const key of [...parsed.searchParams.keys()]) if (key.startsWith('utm_')) parsed.searchParams.delete(key)
    parsed.hash = parsed.hash.startsWith('#readme') ? '' : parsed.hash
    url = parsed.toString()
  } catch {
    return null
  }
  return url.replace(/\.git$/, '').replace(/\/$/, '')
}

/** A licence file with no SPDX id beside it: name it from its opening words, or say so plainly. */
function licenceFromText(text) {
  const t = text.replace(/\s+/g, ' ')
  if (/MIT License|Permission is hereby granted, free of charge/i.test(t)) return 'MIT'
  if (/Apache License,? Version 2\.0/i.test(t)) return 'Apache-2.0'
  if (/Redistribution and use in source and binary forms/i.test(t))
    return /Neither the name/i.test(t) ? 'BSD-3-Clause' : 'BSD-2-Clause'
  return 'Other'
}

// ---------------------------------------------------------------------------------------------
// Modbot itself

function modbot() {
  const licence = fs.readFileSync(path.join(repo, 'LICENSE'), 'utf8')
  if (!/GNU AFFERO GENERAL PUBLIC LICENSE\s+Version 3/.test(licence))
    throw new Error('LICENSE is no longer the AGPL-3.0; update the Modbot entry by hand.')

  // The one place the repository's own address is recorded: the desktop client's update feed.
  const app = fs.readFileSync(path.join(repo, 'src/Modbot.Client.App/Modbot.Client.App.csproj'), 'utf8')
  const feed = app.match(/<ModbotUpdateFeed[^>]*>([^<]+)<\/ModbotUpdateFeed>/)

  return { name: 'Modbot', licence: 'AGPL-3.0', url: feed ? cleanUrl(feed[1]) : null }
}

// ---------------------------------------------------------------------------------------------
// Services, data and design work that no package manifest records. Keep this list short and true.

const services = [
  {
    name: 'VRChat API',
    url: 'https://vrchat.com',
    licence: null,
    note: 'Modbot is not affiliated with or endorsed by VRChat Inc.',
  },
  {
    name: 'VRChat API Community specification',
    url: 'https://github.com/vrchatapi/specification',
    licence: 'MIT',
    note: null,
  },
  {
    name: 'VRChat API Community C# library',
    url: 'https://github.com/vrchatapi/vrchatapi-csharp',
    licence: 'MIT',
    note: null,
  },
  { name: 'Discord', url: 'https://discord.com', licence: null, note: null },
  { name: 'Discord.Net', url: 'https://github.com/Discord-Net/Discord.Net', licence: 'MIT', note: null },
  { name: 'PostgreSQL', url: 'https://www.postgresql.org', licence: 'PostgreSQL', note: null },
  { name: '.NET', url: 'https://github.com/dotnet/runtime', licence: 'MIT', note: null },
  { name: 'ASP.NET Core', url: 'https://github.com/dotnet/aspnetcore', licence: 'MIT', note: null },
  {
    name: 'OpenVR',
    url: 'https://github.com/ValveSoftware/openvr',
    licence: 'BSD-3-Clause',
    note: 'SteamVR and OpenVR are trademarks of Valve Corporation. Modbot is not affiliated with or endorsed by Valve.',
  },
  { name: 'shadcn/ui', url: 'https://github.com/shadcn-ui/ui', licence: 'MIT', note: null },
  { name: 'Lucide icons', url: 'https://lucide.dev', licence: 'ISC', note: null },
  { name: 'IBM Plex fonts', url: 'https://github.com/IBM/plex', licence: 'OFL-1.1', note: null },
  { name: 'Google Fonts', url: 'https://fonts.google.com', licence: null, note: null },
  { name: 'Inter font', url: 'https://github.com/rsms/inter', licence: 'OFL-1.1', note: null },
]

// ---------------------------------------------------------------------------------------------
// NuGet

/** Which part of Modbot a project belongs to. Anything not listed here is server code. */
const DESKTOP = new Set(['Modbot.Client', 'Modbot.Client.App', 'Modbot.Overlay'])

function nuget() {
  const src = path.join(repo, 'src')
  const projects = fs
    .readdirSync(src)
    .filter((p) => fs.existsSync(path.join(src, p, `${p}.csproj`)))
    .sort()

  const direct = new Set()
  const found = new Map()

  for (const project of projects) {
    const csproj = fs.readFileSync(path.join(src, project, `${project}.csproj`), 'utf8')
    for (const m of csproj.matchAll(/<PackageReference\s+Include="([^"]+)"/g)) direct.add(m[1].toLowerCase())

    const assetsFile = path.join(src, project, 'obj/project.assets.json')
    if (!fs.existsSync(assetsFile))
      throw new Error(`${project} has not been restored. Run "npm run credits", which restores first.`)
    const assets = JSON.parse(fs.readFileSync(assetsFile, 'utf8'))
    const folders = Object.keys(assets.packageFolders)

    for (const [key, library] of Object.entries(assets.libraries)) {
      if (library.type !== 'package') continue
      const slash = key.lastIndexOf('/')
      const name = key.slice(0, slash)
      const version = key.slice(slash + 1)

      const entry = found.get(name.toLowerCase()) ?? { name, versions: new Set(), parts: new Set(), dir: null }
      entry.versions.add(version)
      entry.parts.add(DESKTOP.has(project) ? 'Desktop' : 'Server')
      entry.dir ??= folders.map((f) => path.join(f, library.path)).find((d) => fs.existsSync(d))
      found.set(name.toLowerCase(), entry)
    }
  }

  return [...found.values()]
    .map((entry) => {
      if (!entry.dir) throw new Error(`${entry.name} is not in the NuGet packages folder. Restore first.`)
      const nuspecFile = fs.readdirSync(entry.dir).find((f) => f.endsWith('.nuspec'))
      const nuspec = fs.readFileSync(path.join(entry.dir, nuspecFile), 'utf8')
      const tag = (name) => nuspec.match(new RegExp(`<${name}>([^<]*)</${name}>`))?.[1]

      const licenceTag = nuspec.match(/<license type="(expression|file)"[^>]*>([^<]+)<\/license>/)
      let licence
      if (licenceTag?.[1] === 'expression') licence = licenceTag[2].trim()
      else if (licenceTag?.[1] === 'file') {
        const file = path.join(entry.dir, licenceTag[2].trim())
        licence = fs.existsSync(file) ? licenceFromText(fs.readFileSync(file, 'utf8')) : 'Other'
      } else licence = tag('licenseUrl') ? 'Other' : 'Unknown'

      const repository = cleanUrl(nuspec.match(/<repository[^>]*\surl="([^"]+)"/)?.[1])
      const url = cleanUrl(tag('projectUrl')) ?? repository
      const versions = [...entry.versions].sort((a, b) => a.localeCompare(b, 'en', { numeric: true }))

      return {
        name: entry.name,
        version: versions.join(', '),
        licence,
        url,
        source: repository && repository !== url ? repository : null,
        direct: direct.has(entry.name.toLowerCase()),
        usedBy: [...entry.parts].sort(),
      }
    })
    .sort(byName)
}

// ---------------------------------------------------------------------------------------------
// npm

/** The package directory that owns a file inside node_modules, even a nested one. */
function packageDirOf(file) {
  const normal = file.replace(/\\/g, '/')
  const m = normal.match(/^(.*\/node_modules\/(?:@[^/]+\/)?[^/]+)\//)
  return m ? m[1] : null
}

function npmPackage(dir, direct) {
  const json = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'))
  const licence =
    typeof json.license === 'string'
      ? json.license
      : json.license?.type ?? (Array.isArray(json.licenses) ? json.licenses.map((l) => l.type).join(' OR ') : 'Unknown')
  const repository = cleanUrl(typeof json.repository === 'string' ? json.repository : json.repository?.url)
  const url = cleanUrl(json.homepage) ?? repository ?? `https://www.npmjs.com/package/${json.name}`
  return {
    name: json.name,
    version: json.version,
    licence,
    url,
    source: repository && repository !== url ? repository : null,
    direct,
  }
}

async function npm() {
  const manifest = JSON.parse(fs.readFileSync(path.join(web, 'package.json'), 'utf8'))
  const viteConfig = fs.readFileSync(path.join(web, 'vite.config.ts'), 'utf8')
  const plugins = new Set(
    [...viteConfig.matchAll(/from\s+'([^'.][^']*)'/g)].map((m) => m[1]).filter((n) => !n.startsWith('node:')),
  )

  const dirs = new Set()
  await build({
    root: web,
    configFile: path.join(web, 'vite.config.ts'),
    logLevel: 'error',
    build: { write: false, emptyOutDir: false },
    plugins: [
      {
        name: 'modbot-credits',
        generateBundle(_, bundle) {
          for (const chunk of Object.values(bundle)) {
            if (chunk.type !== 'chunk') continue
            for (const id of Object.keys(chunk.modules)) {
              const dir = packageDirOf(id)
              if (dir) dirs.add(dir)
            }
          }
        },
      },
    ],
  })
  if (dirs.size === 0) throw new Error('The build reported no packages. Has "npm ci" been run?')

  const runtimeDirect = Object.keys(manifest.dependencies ?? {}).filter((n) => !plugins.has(n))
  for (const name of runtimeDirect) dirs.add(path.join(web, 'node_modules', name).replace(/\\/g, '/'))

  const shipped = new Map()
  for (const dir of dirs) {
    const pkg = npmPackage(dir, false)
    pkg.direct = runtimeDirect.includes(pkg.name)
    shipped.set(`${pkg.name} ${pkg.version}`, pkg)
  }

  const tools = [...Object.keys(manifest.devDependencies ?? {}), ...Object.keys(manifest.dependencies ?? {}).filter((n) => plugins.has(n))]
  const buildTools = tools.map((name) => {
    const { direct: _direct, ...pkg } = npmPackage(path.join(web, 'node_modules', name), true)
    return pkg
  })

  return { web: [...shipped.values()].sort(byName), buildTools: buildTools.sort(byName) }
}

// ---------------------------------------------------------------------------------------------

const { web: webPackages, buildTools } = await npm()
const credits = {
  modbot: modbot(),
  services,
  nuget: nuget(),
  npm: webPackages,
  buildTools,
}

const text = `${JSON.stringify(credits, null, 2)}\n`

// The page must never show an email address. Package manifests sometimes carry one in a link.
const email = text.match(/[\w.+-]+@[\w-]+\.[\w.-]+/)
if (email) throw new Error(`An email address found its way into the credits: ${email[0]}`)

fs.writeFileSync(out, text)
console.log(
  `Wrote ${path.relative(repo, out)}: ${credits.nuget.length} NuGet, ${credits.npm.length} web, ${buildTools.length} build tools.`,
)
