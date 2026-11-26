# win_wlan_toggle

Small Windows 11 utility to take the Wi-Fi link down for a short, bounded time and
**always** bring it back — on demand (tray / CLI) or on a periodic schedule. Built for lab
tests that need controlled WLAN disconnections, including when driven over an RDP session
that itself depends on that Wi-Fi link.

## Status

| Milestone | State |
|---|---|
| 1. Core library (state machine, scheduler, journal, config, CSV log) + tests | done |
| 2. Windows `IWlanController` implementations (adapter / disassociate / radio) | todo |
| 3. Service + named-pipe IPC + crash recovery | todo |
| 4. Tray app + CLI | todo |
| 5. Installer + release workflow | todo |

## Design in one paragraph

The code that disables/re-enables the adapter runs in a service (LocalSystem), *not* in the
tray, so an RDP logoff or tray crash can't strand the machine offline. Before the link is
taken down, a journal entry with the absolute re-enable deadline is written to disk; it is
cleared only after the link is verified up. On start-up (and resume) the journal is replayed.
Down time is capped (`MaxDown`, default 120 s) even across extensions and recovery, and
cancelling a caller shortens an outage — it never prevents the re-enable.

## Layout

- `src/WlanToggle.Core` — platform-independent logic (`OutageManager`, `PeriodicScheduler`,
  `IWlanController`, journal, CSV log, config).
- `tests/WlanToggle.Core.Tests` — xUnit tests using a fake controller and `FakeTimeProvider`.

## Build & test

```bash
dotnet test
```

CI (`.github/workflows/ci.yml`) builds and tests on Windows and Linux. Anything touching a
real adapter can only be verified on a physical Windows machine.
