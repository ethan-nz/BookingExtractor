# Booking Extraction Tool — C# / WinForms / Playwright

A small Windows desktop app. You pick a date on a calendar; the app reads that day's bookings for one practitioner column from the booking website, shows the patient names, and copies them to the clipboard (tab-separated) for pasting into a spreadsheet.

It is a C#/.NET conversion of an earlier Python tool. This revision keeps the behaviour that matters and fixes a number of bugs, privacy problems and fragile spots found in a code review (see [What changed](#what-changed-from-the-first-c-version)).

> **Status:** the revised code has not been compiled or tested against the live booking site by its author. Build it, try it, and report any compiler messages or surprises.

## Contents

- [Requirements](#requirements)
- [Setup](#setup)
- [Logging in](#logging-in)
- [Using the app](#using-the-app)
- [How extraction works](#how-extraction-works)
- [Privacy and security](#privacy-and-security)
- [Settings](#settings)
- [Where files are stored](#where-files-are-stored)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [What changed from the first C# version](#what-changed-from-the-first-c-version)
- [Ideas for learning and next steps](#ideas-for-learning-and-next-steps)

## Requirements

- Windows 10/11, 64-bit
- [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer (the project targets `net8.0-windows`)
- Google Chrome (or Microsoft Edge, which is built into Windows and used as a fallback)
- VS Code with the C# extension, or Visual Studio 2022

**No Playwright browser download is needed.** The app drives your own installed Chrome/Edge over the Chrome DevTools Protocol, so the old `playwright install chromium` step has been removed.

## Setup

```powershell
dotnet restore
dotnet build
dotnet run
```

On the first run the app launches a dedicated Chrome window with its own profile and, if the site shows its login page, asks for your email and password.

## Logging in

You have two options:

1. **Recommended — type it when asked.** If you provide nothing in advance, a login box appears only when the site actually needs a login. The password is kept in memory for that session and is never written to disk. Chrome's saved profile keeps you logged in between runs, so this is rarely needed.
2. **Automatic — a `.env` file.** Copy `.env.example` to `.env` and fill in `HEALTH_EMAIL` and `HEALTH_PASSWORD`. Prefer saving it as `%LOCALAPPDATA%\BookingExtractor\.env` over putting it in the project folder, and never commit it to version control. The app looks in that folder first, then next to the `.exe`, then in the current folder.

Rules for `.env` values: put quotes around them, and put nothing after the closing quote. A real Windows environment variable always wins over the `.env` file.

## Using the app

1. Wait for **Ready. Select a date to fetch bookings.**
2. Click a day. The app fills the site's date box, waits for the table to update and settle, then reads it.
3. The patient list appears in the text box and is copied to the clipboard. The clipboard is cleared automatically after 60 seconds (only if it still contains what the app copied).
4. Any **WARNING** lines at the top of the text box mean the result may be incomplete or stale. Check them before relying on the data.

Names ending in ` @` are NP bookings.

Closing the app does **not** close Chrome, so the next start can reconnect to the same window.

## How extraction works

1. **Chrome** is started with a remote-debugging port (9222) and a dedicated profile folder, or the app reconnects to the Chrome already running there.
2. **Login** happens only if the login box really appears. The app waits for whichever appears first, the login box or the table, instead of guessing with a fixed delay.
3. **Fetching a date:**
   - Record what the table shows now.
   - Type the new date and fire the page's `input` and `change` events.
   - Poll the table text until it has **changed** from before and then **stopped changing** for about 1.5 seconds. An empty table needs about 6 seconds of calm, because "empty" can also mean "still loading".
   - Take the whole table in **one snapshot** (a single piece of JavaScript run in the page), then parse it in C#.
4. **Column selection:** the column whose header is `Gao` (configurable) is located, then shifted by `COLUMN_OFFSET` (default `-1`, inherited from the Python version) to find the matching table cells.
5. **Name cleanup:** dates like `01/11/2026` are removed, the first non-empty line is kept, the name is title-cased, and ` @` is added for NP bookings.
6. **Warnings** are raised for symptoms that can really be detected: the table did not change after choosing a new date, rows with too few cells (merged cells), buttons with no readable name, or the table changing while being read.

## Privacy and security

This tool handles patient names. Please read this section.

- **Clipboard.** Names are placed on the clipboard marked "do not add to Windows clipboard history" and cleared after 60 seconds. This is best effort: test with **Win+V** on your PC. Cloud clipboard sync and clipboard-manager tools may still capture the data.
- **Remote debugging port.** Chrome runs with port 9222 open on `127.0.0.1`. Any program running on the same PC could connect to that logged-in browser. Do not use this tool on a shared or untrusted computer.
- **Password.** Not stored by default. If you use `.env`, the password sits in plain text in that file; protect the folder and keep the file out of version control.
- **Screenshots** of errors are **off** by default because they would contain patient data. Set `SAVE_ERROR_SCREENSHOTS=1` only while debugging, and delete the image afterwards.
- **Logs** contain counts, timings and error messages but deliberately **no patient names and no passwords**. Don't paste log files or screenshots publicly without checking them.
- **Health information in New Zealand** is covered by the Privacy Act 2020 and the Health Information Privacy Code. If this tool is used in a clinic, confirm with your privacy officer that this workflow is allowed.

## Settings

All optional. Set them in `.env` or as Windows environment variables.

| Name | Default | Meaning |
| --- | --- | --- |
| `HEALTH_EMAIL`, `HEALTH_PASSWORD` | none | Automatic login (otherwise a dialog asks) |
| `BOOKING_COLUMN` | `Gao` | Header text of the practitioner column to read |
| `BOOKING_COLUMN_OFFSET` | `-1` | Shift between the header position and the data cells |
| `CHROME_PATH` | auto-detected | Full path of `chrome.exe` or `msedge.exe` |
| `SAVE_ERROR_SCREENSHOTS` | off | Set to `1` to save a screenshot on a fatal error |

Fixed in code (`Program.cs`, `AppConfig`): the site addresses, the debugging port `9222`, and the CSS selector that identifies booking buttons.

## Where files are stored

Everything lives under `%LOCALAPPDATA%\BookingExtractor\` (paste that into File Explorer's address bar):

| Path | Contents |
| --- | --- |
| `playwright_profile\` | The dedicated Chrome profile (keeps you logged in) |
| `logs\app.log` | Activity log, about 1 MB maximum, then rotated to `app.log.old` |
| `last_error.png` | Only if screenshots are enabled |

The earlier version kept the profile inside `bin\Debug\...`, where `dotnet clean` would erase it. After upgrading you will be asked to log in once more; the old folder can be deleted.

## Troubleshooting

- **"The browser did not come up with remote debugging enabled"** — a Chrome window using the same profile may already be open without the debugging port. Close all windows of that dedicated Chrome and start again. Also check that nothing else is using port 9222.
- **"Column 'Gao' not found"** — the site layout or your practitioner column name changed. The message lists the headers it found; set `BOOKING_COLUMN` accordingly.
- **Wrong people on the list** — check `BOOKING_COLUMN_OFFSET`. If the list is shifted by one column, this is the first suspect.
- **WARNING: "table did not change after selecting the new date"** — the site was too slow or the date did not apply. Fetch again and compare with the website. Open `logs\app.log` for timings.
- **Login box keeps appearing** — the site rejected the credentials, or the saved session expired. Check them by logging in manually in the Chrome window the app opened.
- **Everything is disabled after an error** — this is intended after a fatal browser error; close and reopen the app. Details are in the log.

## Known limitations

- The app depends on the live site's page structure (Angular components, the `input[name='date']` box, CSS classes such as `btn-outline-success`, the `Gao` header). If the site changes, the app needs the same maintenance as the Python version did.
- The "wait until the table settles" logic is a **best-effort** guard against slow responses, not proof of correctness. A site that stays blank for several seconds could still be misread; the warnings exist to catch the cases it can detect. The most reliable improvement is to wait for a specific signal from the site (a loading spinner disappearing, or the exact data request completing), found with Chrome DevTools > Network.
- Names are title-cased, so `McDonald` becomes `Mcdonald`.
- The `@` NP marker rule (`np` not touching other letters) is an assumption; confirm it matches how NP appears on the real site.
- Two page routes (the "cache table" page, and the "Booking Table" link followed after a fresh login) are assumed to show the same table.

## What changed from the first C# version

**Privacy and security**
- Password prompt instead of a mandatory plain-text `.env`
- Clipboard excluded from history and auto-cleared
- Screenshots opt-in; logs contain no patient names
- Profile, logs and data moved to `%LOCALAPPDATA%`

**Bugs fixed**
- NP detection no longer matches names such as "Jinping"
- Passwords containing `#` are no longer cut short in `.env`
- Real environment variables are no longer overwritten by `.env`
- Date text is always `dd/MM/yyyy` with real slashes, regardless of PC region
- A fatal error no longer leaves a clickable calendar that hangs forever
- A request lost to a dropped connection is retried
- Reconnecting is limited to 5 attempts instead of looping forever
- Empty days no longer cause a clipboard error
- Day buttons are disposed (no resource leak)
- The "Booking Table" URL is built safely with `Uri`

**Reliability and design**
- One-shot page snapshot instead of dozens of round trips
- Table-settle wait and honest warnings instead of a self-checking "mismatch" count
- async/await with `Channel<T>` instead of blocking calls
- Typed messages (`StatusMsg`, `FetchedMsg`, ...) instead of one record with eight optional fields
- File logging, global exception handlers, Edge fallback

## Ideas for learning and next steps

1. **Split `Program.cs` into several files** (`AppConfig.cs`, `BookingParser.cs`, `PlaywrightWorker.cs`, `CalendarApp.cs`). The code is already divided into these sections.
2. **Write unit tests** for `BookingParser`; it needs no browser. With xUnit, test cases such as `"Jinping Wang"` (no `@`), `"Lee Min NP"` (adds `@`), and a name preceded by a date line.
3. **Replace the settle heuristic** with a precise wait for the site's own loading signal.
4. **Upgrade the target framework** to the current LTS (`net10.0-windows`) once the .NET 10 SDK is installed.
5. **Restart the worker** after a fatal error instead of requiring a restart of the app.
