# Modbot.Client.App

The Windows desktop client. It reads VRChat's log, sends events to the paired Modbot server, shows
the overlay and keeps the Modbot Cloud backup.

Its settings are in the app. Where the Cloud backup goes comes from the paired server, through
`MODBOT_CLOUD_ENDPOINT` and `MODBOT_CLOUD_DISABLED` on that server.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `MODBOT_CLIENT_LOG_LEVEL` | No | `Verbose` | The lowest level written to the client's own log (`%APPDATA%\Modbot\logs`): `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Verbose`. |

## Build settings

| Setting | Where | What it does |
|---|---|---|
| `ModbotRelease` | MSBuild (`-p:ModbotRelease=2026.9.0`) | The release version stamped on the client. The client release workflow sets it. |
