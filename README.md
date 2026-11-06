# Booking Extraction Tool — C# / WinForms / Playwright

This is a C#/.NET conversion of the supplied Python booking extraction tool.

## Stack

- .NET 8 WinForms
- Microsoft.Playwright 1.61.0
- Chrome controlled through a persistent dedicated profile and CDP port 9222

Microsoft.Playwright 1.61.0 is the current package version verified on 18 August 2026.

## Setup

1. Install the .NET 8 SDK on Windows.
2. Open this folder in Visual Studio 2022 / VS Code.
3. Copy `.env.example` to `.env` and fill in `HEALTH_EMAIL` and `HEALTH_PASSWORD`.
4. Run:

   ```powershell
   dotnet restore
   dotnet build
   ```

5. Install the Playwright browser binaries once:

   ```powershell
   pwsh bin\Debug\net8.0-windows\playwright.ps1 install chromium
   ```

   If the generated script path differs, use the Playwright installation command exposed by the package in your build output.

6. Run the application with `dotnet run` or start the built `.exe`.

## Behavioral compatibility notes

The conversion intentionally preserves the important behaviors of the Python version:

- dedicated `playwright_profile` Chrome profile
- Chrome launched independently with CDP on port 9222
- reconnect to the existing Chrome/tab when possible
- login only when the login field is actually visible
- same `Gao` table-column targeting and `COLUMN_OFFSET = -1`
- same `dd/MM/yyyy` date format
- same booking-button selectors
- same NP (`@`) tagging and name cleanup behavior
- mismatch/skipped-item detection
- clipboard output separated by tabs
- Chrome is not closed when the C# app exits

## Important blind spot

The application depends on the live booking site's current DOM/selectors and request behavior. In particular, the date-selection logic still assumes the site's date input accepts `dd/MM/yyyy` and that a successful booking-related response can be observed. The original Python comments already flag this as site-specific. If the website changes its Angular components, selectors, or XHR URLs, the C# application will need the same kind of maintenance as the Python version.
