# Installing the desktop client

The Modbot desktop client is a small Windows program that sits in the tray, reads VRChat's own log
file, and tells your group's Modbot which of the group's instances you are standing in. This page
is for testers: how to get it onto a PC, run it, keep it up to date, and take it off again.

It runs on 64-bit Windows 10 or 11. It does not need .NET or anything else installed first, and it
does not need administrator rights.

## Download

Every release is on the project's GitHub **Releases** page. Each one has three downloads:

| File | What it is |
|---|---|
| `Modbot-win-Setup.exe` | **The installer.** This is the one to use. |
| `Modbot-win-Portable.zip` | The same program with no installer, for a PC where you cannot or would rather not install anything. Unzip it anywhere and run `Modbot.exe`. It still updates itself. |
| `Modbot-….nupkg`, `releases.win.json`, `RELEASES` | What installed clients read when they check for updates. You never need these. |

## Install

1. Run `Modbot-win-Setup.exe`. There are no questions to answer; it installs to your own user
   folder (`%LOCALAPPDATA%\Modbot`), adds **Modbot** to the Start Menu and to *Installed apps*,
   and opens the client.
2. If Windows shows a blue **"Windows protected your PC"** box, see the next section.

### About the "Windows protected your PC" box

Test builds are not yet code-signed, so Windows SmartScreen has no publisher to check and warns
about every download it has not seen before. That warning is about *reputation*, not about
anything the file does. To get past it, press **More info**, then **Run anyway**. Some browsers
also ask whether to keep the download; keep it.

Windows Defender may occasionally quarantine an unsigned program that reads another program's
files and talks to the network, which is exactly what this client does. If Modbot vanishes after
install, look in *Windows Security → Protection history*. Please tell the maintainer when this
happens; it is the reason signed builds are on the list.

Signed builds, when they arrive, will not show either warning. Until then, the honest answer to
"should I trust this?" is: the source is public, the client's window shows everything it has sent,
and this page tells you exactly what it touches.

## First run

The client opens its window and puts an icon in the tray (the icons near the clock). Closing the
window leaves it running in the tray; **Quit** in the tray menu stops it.

Running it once is also what tells Windows that Modbot pairing links open this program. Nothing
else on the machine is changed.

It is not yet reporting to anybody. To connect it to your group's Modbot, follow
[Pairing the desktop client](pairing-the-desktop-client.md). Pairing takes about ten seconds and
happens in your browser.

## Log backup to Modbot Cloud

**On by default.** The client sends every line of VRChat's log it reads to Modbot Cloud, from every
instance you are in, including private and friends-only ones. VRChat's log contains the display
names and user ids of the people around you, the avatars they switch to, and the instances you
join, including what someone would need to join a private instance. It is kept so the project can
see trends across VRChat and read old lines again when the client learns new things.

- Turn it off in the client under **Settings**. That stops sending at once and deletes anything
  queued. Turning it back on sends only from that moment.
- Your Windows user folder name is removed from lines before they are sent, only log file names
  (not folders) are sent, and nothing from Modbot's own log or your pairings is sent.
- Your client is identified to Modbot Cloud by a random install id, not your name or VRChat account.
- Lines wait in `%APPDATA%\Modbot\cloud` while you are offline, up to 100 MB; past that the oldest go.
- A group's server can turn it off for everyone paired with it, or send it to its own Cloud.

## Where things are

| | |
|---|---|
| The program | `%LOCALAPPDATA%\Modbot` (`C:\Users\<you>\AppData\Local\Modbot`) |
| Its log | `%APPDATA%\Modbot\logs\client-<date>.log`, one file a day, the last seven kept |
| Pairings, queued observations, the record of what has been sent, settings | `%APPDATA%\Modbot` |
| Log lines waiting to go to Modbot Cloud, and its install id | `%APPDATA%\Modbot\cloud`, `%APPDATA%\Modbot\cloud-installs.json` |

The log is the first thing to send when reporting a problem. It never leaves your machine on its
own; the client does not upload its own log. Paste `%APPDATA%\Modbot\logs` into the address bar of
File Explorer to get there. The first line of every run says which version started, which is
worth checking against the Releases page when something looks old.

## Updates

The client checks for a newer version shortly after it starts and every four hours after that.
When there is one it is downloaded in the background, and the window shows a line saying that
version is ready and will be installed **the next time Modbot starts**.

Nothing is installed while Modbot is running. If you are in VRChat with the client reporting, it
keeps reporting; quit from the tray icon and open Modbot again whenever suits you, and the new
version comes up. Your pairings are kept, because updating never touches `%APPDATA%\Modbot`.

To stop the client checking at all — a group that pins a version, or a PC that must not call out
— create `%APPDATA%\Modbot\settings.json` containing:

```json
{ "checkForUpdates": false }
```

and restart the client. (The same file can hold the `pairingPage` setting from the pairing page;
both are optional.)

**For now:** while the project's repository is private, an installed client cannot read its
releases and the check fails quietly; the log says `Could not check for updates` and the client
carries on. Until the feed is public, get new versions from the Releases page and run the new
`Setup.exe` over the old install. Pairings survive that too.

## Uninstall

*Settings → Apps → Installed apps → Modbot → Uninstall*, the same as any other program. That
removes the program, its Start Menu entry and everything under `%LOCALAPPDATA%\Modbot`.

It **leaves `%APPDATA%\Modbot`** — your pairings, the log files, and the record of what has been
sent — so that reinstalling picks up where you left off and a tester trying a new build does not
have to pair again every time. To remove every trace, delete that folder too. The one registry
key the client made, `HKEY_CURRENT_USER\Software\Classes\modbot-client`, is left pointing at a
file that no longer exists; that is harmless, and deleting it is safe at any time.

Unpairing before you uninstall is polite but not required: the group's operator can also revoke
the client from their side, and a token that is never used again does nothing.

## Building it yourself

If you would rather not run a binary from the Releases page:

```
dotnet publish src/Modbot.Client.App -c Release -p:PublishProfile=win-x64
```

produces the same folder the installer contains, under `artifacts/client/publish`. Run
`Modbot.exe` from there. A copy started that way knows it is not installed and does not check for
updates.
