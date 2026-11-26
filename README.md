# win_wlan_toggle

Small Windows 11 utility to take the Wi-Fi link down for a short, bounded time and
**always** bring it back — on demand (tray / CLI) or on a periodic schedule. Built for lab
tests that need controlled WLAN disconnections, including when driven over an RDP session
that itself depends on that Wi-Fi link.

## Getting the exe

Download `WlanToggle-win-x64.zip` from the repository's **Releases** page (no login needed), unzip, run.
To publish a new release: `git tag v0.1.0 && git push origin v0.1.0` — the `Release` workflow tests, builds the
single-file exe and attaches it (plus a `.sha256`) to a GitHub Release. Tags containing a `-` (e.g. `v0.1.0-rc1`)
are marked as pre-releases. Every push also uploads the exe as a CI artifact (login required to download).

## Using it

Run `WlanToggle.exe` (it asks for administrator rights: disabling an adapter needs them). An icon appears in
the notification area:

- **Left click** — disconnect Wi-Fi now and reconnect after the configured time.
- **Right click** — settings menu:
  - *Disconnect mode*: **Adapter disable** (netsh), **Disassociate** (Native Wifi disconnect/connect, adapter
    stays enabled), **Radio off** (WinRT Radios API).
  - *Reconnect after*: 5 s, **10 s (default)**, 30 s, 1 min, 5 min, 10 min.
  - *Enable periodic disconnect*: toggle. The first cycle happens one interval after enabling.
  - *Periodic disconnect interval*: 1, 5, 10, 15, 30 min, 1 hour. (Must be longer than the reconnect time.)
  - *Reconnect now* (panic), *Open log folder*, *Exit* (reconnects first).

Settings, the recovery journal and a CSV log of every outage (`outages.csv`: actual down time, time until the
link was back up, attempts) live in `%ProgramData%\WlanToggle`.

### Safety net (for RDP over the same Wi-Fi)

1. A journal with the re-enable deadline is written *before* the link goes down, and replayed on next start.
2. Before each outage a one-shot **SYSTEM scheduled task** (`WlanToggleRestore`) is registered to run
   `WlanToggle.exe restore` 30 s after the deadline. It is removed once the link is verified up, so it only
   ever fires if the tray process died, was logged off, or could not restore the link itself.
3. Re-enable is verified (link operationally up), retried, and escalated (adapter bounce, reconnect).
4. Down time is capped (`Outage.MaxDown`, default 15 min); the periodic schedule switches itself off if the
   link ever fails to come back.

## Status

| Milestone | State |
|---|---|
| 1. Core library (state machine, scheduler, engine, journal, config, CSV log) + tests | done |
| 2. Windows controllers: adapter / disassociate / radio, scheduled-task backstop | written, **needs testing on real hardware** |
| 3. Tray app (WinForms) | written, compiles; **needs testing on real hardware** |
| 4. Release workflow (tag → GitHub Release with exe) | done |
| 5. Auto-start at logon, resume-from-sleep handling | todo |

## Design in one paragraph

All behaviour lives in `WlanToggle.Core` (`WlanToggleEngine` → `OutageManager` + `PeriodicScheduler`), behind an
`IWlanController` interface, so it is unit-tested on Linux with a fake controller and a fake clock. The Windows
project implements the controllers and the scheduled-task backstop; the tray project is a thin UI over the
engine. Re-enabling never uses the caller's cancellation token: cancelling shortens an outage, it never
prevents the reconnect.

## Layout

- `src/WlanToggle.Core` — platform-independent logic (`WlanToggleEngine`, `OutageManager`, `PeriodicScheduler`,
  `IWlanController`, journal, CSV log, config).
- `src/WlanToggle.Windows` — `AdapterController`, `DisassociateController`, `RadioController`, `ScheduledTaskGuard`.
- `src/WlanToggle.Tray` — the notification-area app (`WlanToggle.exe`, also the headless `restore` verb).
- `tests/WlanToggle.Core.Tests` — xUnit tests using a fake controller and `FakeTimeProvider`.

## Build & test

```bash
dotnet test
```

CI (`.github/workflows/ci.yml`) tests on Linux, and on Windows builds everything and uploads a self-contained
`WlanToggle.exe` as a workflow artifact. Anything touching a real adapter can only be verified on a physical
Windows machine.
