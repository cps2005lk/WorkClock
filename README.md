# WorkClock

A Windows system-tray app that shows your current office attendance status
(in/out) as a colored icon, using your organization's real Azure AD login
(once), then reusing the session and refreshing on a timer.

It's built against a generic "attendance portal" HTTP API - point it at
your own portal's URL via config (see [Configuration](#configuration)).

- **Green** icon - your latest punch is an `in` (you're currently in).
- **Red** icon - your latest punch is an `out`.
- **Gray** icon - unknown / no data yet.

The in/out status is derived from the most recent attendance punch, not from
any location text the portal returns.

## Setup

```bash
dotnet restore
dotnet build
pwsh bin/Debug/net10.0-windows/playwright.ps1 install chromium
```

Copy the example config and fill in your own portal details:

```bash
cp appsettings.example.json appsettings.json
```

Then edit `appsettings.json` - at minimum set `EmployeeId` and `BaseUrl` to
match your attendance portal (see [Configuration](#configuration)).

## Run

```bash
dotnet run
```

- First run: a real Chrome window opens for your organization's Azure AD
  sign-in (MFA too, if asked). Once logged in, the browser closes and an
  icon appears in your system tray.
- **Hover over the tray icon** to see the current status, e.g.
  `In - Main Entrance (total inside 02:15)`.
- **Right-click the tray icon** for:
  - *Refresh now* - fetch the latest status immediately.
  - *Log in again* - force a fresh login (useful if session expired).
  - *Remind me every Nh while In* - toggle the reminder feature on/off
    (see below). Persisted across restarts.
  - *Exit* - close the app.
- It auto-refreshes on the configured interval, and auto re-logs in if the
  session has expired.

## "Out" balloon

When your status changes to **Out**, a tray balloon notification pops up.
It only fires on the transition into Out, not on every refresh.

## Time-inside reminders

Some attendance portals under-report total time inside when an `out` punch
is missing and two `in` punches end up consecutive - only the time after the
*last* `in` gets counted, so the earlier stretch is lost. This app doesn't
own the official record, so it can't fix that number directly. Instead it
helps you avoid the loss:

- **Reminder** - while you're clocked in, the app pops a window (which stays
  until you dismiss it) showing how long your current open session has been
  running, prompting you to do a dummy `out` + `in` to "checkpoint" your time
  so a later stray `in` can't erase it.
  - The **first** reminder fires once you've been in for
    `ReminderIntervalHours`.
  - After that it **repeats every `ReminderFollowupMinutes`** (a shorter nag,
    flagged as `⚠ Still In - reminder #N`) so a missed reminder is caught
    sooner. Doing a dummy `out` + `in` creates a new `in` punch, which resets
    the cycle back to the full interval.
- **Multi-monitor + sound** - the reminder appears on **every monitor** at
  once and plays an alert sound, so it can't be missed. Dismissing any copy
  closes them all.
- **Bug detection** - if the app sees two `in` punches with no `out` between
  them in your actual data, it warns you immediately (once per occurrence)
  so you can add a corrective punch while you remember.

Toggle the whole feature from the tray menu, or via config (below).

## Configuration

All settings live in `appsettings.json` next to the app - edit it and
restart; no recompile needed. Any missing key falls back to a built-in
default. `appsettings.json` is gitignored since it holds your employee ID
and portal URL; `appsettings.example.json` is the template to copy from.

```json
{
  "EmployeeId": "YOUR_EMPLOYEE_ID",
  "BaseUrl": "https://your-attendance-portal.example.com",
  "SessionFile": "session.json",
  "RefreshIntervalMinutes": 5,
  "LoginTimeoutMinutes": 3,
  "BalloonDurationSeconds": 5,
  "RemindersEnabled": true,
  "ReminderIntervalHours": 2,
  "ReminderFollowupMinutes": 30
}
```

`RemindersEnabled` is also written back here when you toggle the reminder
from the tray menu.

## Run at Windows startup (optional)

1. Build in Release: `dotnet publish -c Release -r win-x64 --self-contained`
2. Press `Win + R`, type `shell:startup`, Enter.
3. Put a shortcut to the published `.exe` in that folder.

## Notes

- `session.json` contains your login session - treat it like a password.
  It's gitignored.
- Tray tooltip text is capped at 127 characters by Windows; longer status
  text gets truncated.
- Check with your IT/admin that automated access is allowed under your
  company's policy.
