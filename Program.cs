// =============================================================================
//  BookingExtractor — revised version, annotated for a beginner learning C#
// =============================================================================
//
//  HOW TO READ THE COMMENTS
//    "LEARN:" marks a C# concept or a general programming idea.
//    "WHY:"   explains a design decision (usually something fixed from the
//             original version).
//
//  FILE MAP (top to bottom)
//    1. AppConfig        – settings and folder locations
//    2. AppLog           – a tiny log file writer (no patient names in it!)
//    3. Message types    – what the browser worker and the window say to each other
//    4. DotEnv           – reads the optional .env file
//    5. BookingParser    – pure text-cleaning logic (easy to unit test)
//    6. PlaywrightWorker – drives Chrome on a background task
//    7. LoginDialog      – asks for the password only when it is really needed
//    8. CalendarApp      – the window the user sees
//    9. Program          – Main(), where the app starts
//
//  NOTE: This file has NOT been compiled or run against the live booking site by
//  the author (no .NET SDK or network access was available). Build it, and if the
//  compiler reports anything, paste the exact messages back for a quick fix.
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Playwright;

// LEARN: "file-scoped namespace" (C# 10). One line instead of wrapping the
// whole file in { }. Everything below belongs to the BookingExtractor namespace.
namespace BookingExtractor;

// =============================================================================
// 1. CONFIGURATION
// =============================================================================

// LEARN: "internal static class" = a bag of related members that is never
// instantiated with `new`. "internal" means visible only inside this project.
internal static class AppConfig
{
    // LEARN: `const` values are fixed at compile time. Use them for things that
    // never change while the program runs.
    public const string BaseUrl = "https://booking.tianlan.co.nz";
    public const string CacheUrl = BaseUrl + "/booking/cache-table";
    public const int CdpPort = 9222; // Chrome DevTools Protocol port Playwright connects to.

    // WHY: These CSS classes decide which buttons count as bookings. They are
    // styling classes from the site (Bootstrap), so if the site changes its look,
    // bookings could silently disappear. Keeping them in ONE place makes that
    // easy to fix.
    public const string ButtonSelector =
        "button.btn-outline-success, button.btn-outline-primary, button.btn-outline-secondary";

    // LEARN: A property with only a getter (`get { ... }`) can compute its value
    // each time it is read. Here we read an environment variable and fall back
    // to a default, so the column name can change without rebuilding.
    public static string MyColumnName
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("BOOKING_COLUMN")?.Trim();
            return string.IsNullOrEmpty(value) ? "Gao" : value;
        }
    }

    // The table cells line up one position to the LEFT of the header that holds
    // the column name. This is inherited from the original Python version.
    // VERIFY it against the live page if the site layout ever changes.
    public static int ColumnOffset =>
        int.TryParse(Environment.GetEnvironmentVariable("BOOKING_COLUMN_OFFSET"), out var offset) ? offset : -1;

    // WHY: The original stored the Chrome profile and screenshots next to the
    // .exe (bin\Debug\...). `dotnet clean` deletes that folder, and a program
    // installed under "Program Files" cannot write there. %LOCALAPPDATA% is the
    // standard per-user place for app data.
    // NOTE: Moving the profile means the first run asks you to log in again.
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookingExtractor");

    public static string ProfileDir => Path.Combine(DataDir, "playwright_profile");
    public static string LogDir => Path.Combine(DataDir, "logs");
}

// =============================================================================
// 2. LOGGING
// =============================================================================

// WHY: With a timing-sensitive web scraper you need a record of what happened.
// PRIVACY RULE: log counts and timings, NEVER patient names or passwords.
internal static class AppLog
{
    // LEARN: `lock (Gate)` lets only one thread at a time run the code inside.
    // Both the window thread and the worker task write to the log, so without a
    // lock two writes could collide.
    private static readonly object Gate = new();

    private static string LogFile => Path.Combine(AppConfig.LogDir, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.LogDir);

                // Keep the log from growing forever: at ~1 MB, start a new one.
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(LogFile, LogFile + ".old", overwrite: true);

                File.AppendAllText(LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // LEARN: An empty catch is normally a bad smell, but here it is
            // deliberate: a problem WRITING the log must never crash the app.
        }
    }
}

// =============================================================================
// 3. MESSAGE TYPES (how the worker and the window talk to each other)
// =============================================================================

// LEARN: A `record` is a short way to declare a class that mostly just holds
// data. The compiler writes the constructor, equality and ToString for you.
//
// WHY: Passwords must never end up in logs. A record's automatic ToString()
// prints EVERY property, including the password, so we override it.
internal sealed record LoginCredentials(string Email, string Password)
{
    public override string ToString() => $"LoginCredentials {{ Email = {Email}, Password = *** }}";
}

// WHY: The original used ONE record (`WorkerResult`) with eight optional fields,
// so impossible states (Ready AND Fatal at once) were representable and the
// receiving code was a chain of `if`s. A small family of types is clearer:
// each message type can only contain what that message actually needs.
//
// LEARN: `abstract record` = a base type you cannot create directly; each
// message below "is a" WorkerMessage. The window then uses a `switch` on the
// type (pattern matching) to decide what to do.
internal abstract record WorkerMessage;

internal sealed record StatusMsg(string Text) : WorkerMessage;

internal sealed record ReadyMsg : WorkerMessage;

internal sealed record ErrorMsg(string Summary) : WorkerMessage;   // this one request failed, worker lives on

internal sealed record FatalMsg(string Summary) : WorkerMessage;   // worker has stopped for good

internal sealed record FetchedMsg(
    string Date,
    IReadOnlyList<string> Bookings,
    IReadOnlyList<string> Warnings,
    string ColumnName) : WorkerMessage;

// LEARN: TaskCompletionSource<T> is a "promise". The worker creates it, sends
// it to the window, then `await`s its Task. The window shows the dialog and
// calls TrySetResult(...) to wake the worker up with the answer.
internal sealed record CredentialsRequestMsg(TaskCompletionSource<LoginCredentials?> Reply) : WorkerMessage;

// WHY: The original used `new WorkerJob("fetch", date)` — a magic string. A
// dedicated type cannot be misspelled.
internal sealed record FetchJob(string Date);

// =============================================================================
// 4. .ENV FILE SUPPORT
// =============================================================================

internal static class DotEnv
{
    public static void Load(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue; // blank line or full-line comment

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart(); // LEARN: [7..] = "from index 7 to the end"

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].Trim();                  // [..equals] = "start up to equals"
            var value = ParseValue(line[(equals + 1)..].Trim());

            // WHY: python-dotenv does NOT overwrite variables that already exist
            // in Windows. The original C# code did, silently. Now real
            // environment variables win, as in the Python version.
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string ParseValue(string value)
    {
        // Quoted value: take what is inside the quotes exactly as written.
        // (Put NOTHING after the closing quote on the same line.)
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        // WHY (bug fix): the original cut the value at ANY '#', so the password
        // "abc#123" became "abc". python-dotenv only starts a comment at a '#'
        // that has WHITESPACE before it. Same rule here.
        var comment = Regex.Match(value, @"\s#");
        return comment.Success ? value[..comment.Index].TrimEnd() : value;
    }
}

// =============================================================================
// 5. PURE TEXT LOGIC (no browser, no window — easy to test)
// =============================================================================

// WHY: Logic that only transforms text should live in its own small class. You
// can then unit-test it with examples and no browser involved.
internal static class BookingParser
{
    private static readonly Regex DatePattern = new(@"\d{2}/\d{2}/\d{4}", RegexOptions.Compiled);

    // WHY (bug fix): the original used rawText.Contains("np"), which also matches
    // INSIDE names ("Jinping", "Wenping", "Hanpeng") and wrongly tags them as NP.
    // This pattern means: "np" that is NOT directly next to another letter.
    //   (?<![A-Za-z])  = not preceded by a letter   (a "negative lookbehind")
    //   (?![A-Za-z])   = not followed by a letter   (a "negative lookahead")
    // Matches:  "NP", "(np)", "np12:30", "Lee - NP".   Does not match: "Jinping".
    // I used "not next to a letter" rather than the simpler \b so that text such
    // as "NP12:30" still counts. CONFIRM how NP appears on the real site.
    private static readonly Regex NpPattern =
        new(@"(?<![A-Za-z])np(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsNp(string rawText) => NpPattern.IsMatch(rawText);

    // Returns the cleaned patient name, or null if nothing usable was found.
    // LEARN: `string?` means "a string OR null". The ? makes the possibility of
    // null visible, so the compiler makes callers handle it.
    public static string? CleanName(string rawText)
    {
        var withoutDates = DatePattern.Replace(rawText, "");

        // WHY: The original split on '\n' and took the first piece even if it
        // was empty (e.g. when the date sat on the first line), producing blank
        // names. We take the first NON-EMPTY line instead.
        var firstLine = withoutDates
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (firstLine is null)
            return null;

        var name = TitleCase(firstLine);
        return IsNp(rawText) ? name + " @" : name;
    }

    // WHY: The original used CultureInfo.CurrentCulture, so results could vary
    // between PCs with different regional settings. InvariantCulture always
    // behaves the same. (ToTitleCase ignores words that are ALL CAPS, so we
    // lower-case first.)
    // KNOWN LIMIT: "McDonald" becomes "Mcdonald". Fine for matching, not perfect.
    private static string TitleCase(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
}

// =============================================================================
// 6. THE BROWSER WORKER
// =============================================================================

// LEARN — why a separate worker? Browser steps take seconds. If they ran on the
// window's own thread, the window would freeze. The worker runs on a background
// Task and talks to the window ONLY through two thread-safe "mailboxes":
//   window  -> worker : a Channel<FetchJob>        ("please fetch this date")
//   worker  -> window : a ConcurrentQueue<WorkerMessage>  (status, results, errors)
// Never touch window controls from the worker; Windows Forms only allows that
// from the thread that created them.
internal sealed class PlaywrightWorker
{
    // --- tuning knobs for "wait until the table has really updated" ----------
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // A normal table must look identical this long before we trust it.
    private static readonly TimeSpan StableFor = TimeSpan.FromMilliseconds(1500);

    // If the table still equals the OLD one, or is EMPTY, it may just be loading.
    // Wait this long before accepting it as "really unchanged / really empty".
    private static readonly TimeSpan UnchangedGiveUp = TimeSpan.FromSeconds(6);

    // Absolute limit: if the table keeps changing for this long, give up.
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(20);

    private const int MaxConsecutiveReconnects = 5;

    // LEARN: HttpClient is meant to be created ONCE and reused. The original made
    // a new one on every check, which can exhaust network sockets.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };

    // LEARN: Channel<T> = a modern async-friendly queue. The worker can
    // `await` a read without blocking a thread. (Replaces BlockingCollection.)
    private readonly Channel<FetchJob> _jobs = Channel.CreateUnbounded<FetchJob>();
    private readonly ConcurrentQueue<WorkerMessage> _messages;

    // LEARN: A CancellationTokenSource is the standard .NET way to say "please
    // stop". Waiting methods accept its token and abort when it is cancelled.
    private readonly CancellationTokenSource _cts = new();

    private Task? _runTask;
    private FetchJob? _pendingJob;                // the request to retry after reconnecting
    private LoginCredentials? _cachedCredentials; // kept in memory only, never saved to disk
    private int _consecutiveReconnects;

    public PlaywrightWorker(ConcurrentQueue<WorkerMessage> messages) => _messages = messages;

    public void Start() => _runTask = Task.Run(() => RunAsync(_cts.Token));

    public void SubmitDate(string date) => _jobs.Writer.TryWrite(new FetchJob(date));

    public void Shutdown()
    {
        _cts.Cancel();
        _jobs.Writer.TryComplete();
    }

    // Gives the worker a moment to finish cleanly when the window closes.
    public void WaitForExit(TimeSpan timeout)
    {
        try { _runTask?.Wait(timeout); }
        catch { /* the worker already logged whatever went wrong */ }
    }

    private void Post(WorkerMessage message) => _messages.Enqueue(message);

    // -------------------------------------------------------------------------
    // Main loop: (re)connect to Chrome, log in if needed, then serve requests.
    // -------------------------------------------------------------------------
    // LEARN: `async Task` + `await`. At each `await`, the method pauses WITHOUT
    // blocking a thread and resumes when the awaited work finishes. The original
    // used `.GetAwaiter().GetResult()` after every call, which blocks instead.
    private async Task RunAsync(CancellationToken ct)
    {
        var firstAttempt = true;

        while (!ct.IsCancellationRequested)
        {
            IPage? page = null;

            try
            {
                // LEARN: `using var` disposes the object when the enclosing block
                // ends. Disposing Playwright shuts down its driver process. It
                // does NOT close Chrome, because Chrome is a separate program.
                using var playwright = await Playwright.CreateAsync();

                if (await CdpIsUpAsync())
                {
                    Post(new StatusMsg("Reconnecting to open Chrome window..."));
                }
                else
                {
                    Post(new StatusMsg(firstAttempt
                        ? "Launching Chrome (first run)..."
                        : "Chrome window is gone — launching a new one..."));
                    await LaunchDetachedBrowserAsync(ct);
                }

                firstAttempt = false;

                var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{AppConfig.CdpPort}");
                var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
                page = PickPage(context) ?? await context.NewPageAsync();

                await LoginIfNeededAsync(page, ct);
                await page.WaitForSelectorAsync("table", new() { Timeout = 25000 });

                // WHY: If we are reconnecting with a request still waiting, the
                // window is still "busy" for it. Sending "Ready" now would
                // re-enable the calendar mid-request.
                if (_pendingJob is null)
                    Post(new ReadyMsg());
                else
                    Post(new StatusMsg("Reconnected — retrying your last request..."));

                await ProcessJobsAsync(page, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // normal shutdown
            }
            catch (ChannelClosedException)
            {
                return; // normal shutdown
            }
            catch (Exception ex) when (IsConnectionLost(ex))
            {
                // LEARN: `catch (...) when (condition)` is an exception FILTER:
                // this block only runs if the condition is true; otherwise the
                // exception continues to the next catch.
                _consecutiveReconnects++;
                AppLog.Error($"Connection lost (attempt {_consecutiveReconnects}): {ex.Message}");

                // WHY: The original reconnected forever. A cap prevents an endless
                // loop (and an endless stream of Chrome launches).
                if (_consecutiveReconnects > MaxConsecutiveReconnects)
                {
                    Post(new FatalMsg("The browser connection keeps dropping. Check that Chrome is running and try again."));
                    return;
                }

                Post(new StatusMsg("Browser connection was lost (window closed, or Chrome discarded the idle tab) — reconnecting..."));

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                await ReportFatalAsync(ex, page);
                return;
            }
        }
    }

    // Serves fetch requests until the connection drops or we are told to stop.
    private async Task ProcessJobsAsync(IPage page, CancellationToken ct)
    {
        while (true)
        {
            FetchJob job;

            // WHY (bug fix): when the connection dropped, the original discarded
            // the user's click. Now the failed job is remembered and retried
            // after reconnecting.
            if (_pendingJob is not null)
            {
                job = _pendingJob;
                _pendingJob = null;
                AppLog.Info($"Retrying {job.Date} after reconnect.");
            }
            else
            {
                job = await _jobs.Reader.ReadAsync(ct); // waits here, without blocking a thread
            }

            try
            {
                var clock = Stopwatch.StartNew();
                var result = await FetchForDateAsync(page, job.Date, ct);

                // Counts and timings only — no names.
                AppLog.Info($"Fetched {job.Date}: {result.Bookings.Count} bookings, " +
                            $"{result.Warnings.Count} warnings, {clock.ElapsedMilliseconds} ms.");

                _consecutiveReconnects = 0;
                Post(result);
            }
            catch (Exception ex) when (IsConnectionLost(ex))
            {
                _pendingJob = job; // remember it, then let RunAsync reconnect
                throw;             // LEARN: plain `throw;` rethrows keeping the original stack trace.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"Fetch of {job.Date} failed: {ex}");
                Post(new ErrorMsg($"Could not fetch {job.Date}:\n{ex.Message}"));
            }
        }
    }

    // WHY: The original treated ANY exception whose message contained "closed"
    // as a lost connection, which is far too broad. We now require a Playwright
    // exception AND one of a few known phrases. (Check your own logs for the
    // exact wording on your machine and adjust this list if needed.)
    private static bool IsConnectionLost(Exception ex)
    {
        if (ex is not PlaywrightException)
            return false;

        string[] phrases =
        {
            "has been closed", "Target closed", "Connection closed",
            "ECONNREFUSED", "browser has disconnected"
        };

        return phrases.Any(p => ex.Message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Fetching one date
    // -------------------------------------------------------------------------
    private static async Task<FetchedMsg> FetchForDateAsync(IPage page, string date, CancellationToken ct)
    {
        var dateInput = page.Locator("input[name='date']");

        // Remember what the page shows BEFORE we change the date. Afterwards we
        // wait until the table has changed from this and then stopped changing.
        var dateBefore = await dateInput.InputValueAsync();
        var tableBefore = await GetTableSignatureAsync(page);

        await dateInput.FillAsync(date);
        await dateInput.DispatchEventAsync("input");
        await dateInput.DispatchEventAsync("change");

        // Sanity check that the box accepted the text. This only proves the text
        // box holds our date; it does NOT prove the table has reloaded. That is
        // what the settle step below is for.
        var dateAfter = await dateInput.InputValueAsync();
        if (!string.Equals(dateAfter, date, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Date field shows '{dateAfter}' instead of '{date}' after filling it. " +
                "The site's date field likely isn't a plain text input and may require its own calendar UI.");
        }

        // STALE-DATA MITIGATION (best effort, not verified against the live site):
        // wait for the table to change AND stop changing.
        var expectChange = !string.Equals(dateBefore, date, StringComparison.Ordinal);
        var (changed, settledSignature) = await WaitForTableToSettleAsync(page, tableBefore, expectChange, ct);

        await page.WaitForSelectorAsync("thead th", new() { State = WaitForSelectorState.Attached, Timeout = 10000 });

        // WHY: The original made dozens of separate round trips to Chrome (one
        // per row, cell and button), and the page could change between them.
        // Here ONE piece of JavaScript runs inside the page and returns the whole
        // table as one consistent snapshot. We then parse it in C#.
        var snapshot = await page.EvaluateAsync<JsonElement>(SnapshotScript, AppConfig.ButtonSelector);

        // LEARN: JsonElement is .NET's read-only view over JSON. GetProperty
        // finds a field; EnumerateArray loops over a JSON array.
        var headers = snapshot.GetProperty("headers").EnumerateArray()
            .Select(h => (h.GetString() ?? "").Trim())
            .ToList();

        var headerIndex = headers.FindIndex(h => string.Equals(h, AppConfig.MyColumnName, StringComparison.Ordinal));
        if (headerIndex < 0)
        {
            throw new InvalidOperationException(
                $"Column '{AppConfig.MyColumnName}' not found in headers: {string.Join(", ", headers)}");
        }

        var colIndex = headerIndex + AppConfig.ColumnOffset;
        if (colIndex < 0)
        {
            throw new InvalidOperationException(
                $"Column offset {AppConfig.ColumnOffset} points before the first column (header index {headerIndex}). " +
                "Check BOOKING_COLUMN_OFFSET.");
        }

        var bookings = new List<string>();
        var rowsTooShort = 0;
        var unreadableButtons = 0;

        foreach (var row in snapshot.GetProperty("rows").EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            if (cells.Count == 0)
                continue; // a row with no <td> at all (spacer, header-like row)

            if (colIndex >= cells.Count)
            {
                rowsTooShort++; // probably merged cells (colspan) shifting the columns
                continue;
            }

            foreach (var buttonText in cells[colIndex].EnumerateArray())
            {
                var name = BookingParser.CleanName(buttonText.GetString() ?? "");
                if (name is null)
                    unreadableButtons++;
                else
                    bookings.Add(name);
            }
        }

        // WHY: The original "mismatch" check compared a count with a second count
        // taken by the SAME selector over the SAME cells, so it could not catch
        // real problems. These warnings report real, detectable symptoms instead.
        var warnings = new List<string>();

        if (rowsTooShort > 0)
            warnings.Add($"{rowsTooShort} row(s) had too few cells (merged cells?) and were skipped — the list may be incomplete.");

        if (unreadableButtons > 0)
            warnings.Add($"{unreadableButtons} booking button(s) had no readable name and were skipped.");

        if (expectChange && !changed)
            warnings.Add("The table did not change after selecting the new date — these may be the PREVIOUS date's bookings. Please check.");

        var finalSignature = await GetTableSignatureAsync(page);
        if (finalSignature != settledSignature)
            warnings.Add("The table changed while it was being read — please fetch again.");

        return new FetchedMsg(date, bookings, warnings, AppConfig.MyColumnName);
    }

    // JavaScript that runs INSIDE the web page. It receives the button CSS
    // selector as `selector` and returns { headers: string[], rows: string[][][] }:
    //   rows[r][c] = the list of button texts found in row r, cell c.
    // LEARN: @"..." is a verbatim string: line breaks and backslashes are kept
    // literally, which suits embedded code. (Only a double quote needs doubling.)
    private const string SnapshotScript = @"selector => {
        const headers = [...document.querySelectorAll('thead th')].map(th => th.innerText.trim());
        const rows = [...document.querySelectorAll('tbody tr')].map(tr =>
            [...tr.querySelectorAll('td')].map(td =>
                [...td.querySelectorAll(selector)].map(b => b.textContent || '')));
        return { headers, rows };
    }";

    // A cheap fingerprint of what the table body currently shows. Two identical
    // fingerprints mean the visible table text is the same.
    private static async Task<string> GetTableSignatureAsync(IPage page)
    {
        var text = await page.EvaluateAsync<string>(
            "() => [...document.querySelectorAll('tbody')].map(t => t.innerText).join('|')");
        return text ?? "";
    }

    // Waits until the table (a) is different from before the date change and
    // (b) has stopped changing for a short while. Returns whether it changed.
    //
    // KNOWN LIMITS — read these:
    //  * A legitimately EMPTY day looks like an "empty while loading" table, so an
    //    empty table is only accepted after UnchangedGiveUp (6 s). Empty days are
    //    therefore slower, by design.
    //  * If the site takes longer than UnchangedGiveUp to respond, we return the
    //    old data WITH a warning (changed == false) rather than hang forever.
    //  * The best fix is to wait for a specific, reliable signal from the site
    //    (a loading spinner disappearing, or the exact data request finishing).
    //    Find it with Chrome DevTools > Network and replace this method.
    private static async Task<(bool Changed, string Signature)> WaitForTableToSettleAsync(
        IPage page, string signatureBefore, bool expectChange, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var latest = await GetTableSignatureAsync(page);
        var lastChangeAt = clock.Elapsed;

        while (clock.Elapsed < SettleTimeout)
        {
            await Task.Delay(PollInterval, ct);

            var current = await GetTableSignatureAsync(page);
            if (current != latest)
            {
                latest = current;
                lastChangeAt = clock.Elapsed; // still moving — restart the "stable" timer
                continue;
            }

            var stableFor = clock.Elapsed - lastChangeAt;
            var changed = latest != signatureBefore;

            // An empty table may just be "loading", so ask for a longer calm period.
            var needed = string.IsNullOrWhiteSpace(latest) ? UnchangedGiveUp : StableFor;
            if (stableFor < needed)
                continue;

            if (!expectChange) return (changed, latest);      // same date requested again
            if (changed) return (true, latest);               // new data, and stable
            if (clock.Elapsed >= UnchangedGiveUp) return (false, latest); // never changed; caller warns
        }

        throw new InvalidOperationException(
            "The booking table kept changing and never settled within 20 seconds.");
    }

    // -------------------------------------------------------------------------
    // Logging in
    // -------------------------------------------------------------------------
    private async Task LoginIfNeededAsync(IPage page, CancellationToken ct)
    {
        await page.GotoAsync(AppConfig.CacheUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var emailBox = page.Locator("[formcontrolname='email']");
        var table = page.Locator("table");

        // WHY: The original waited 8 s for the login box on EVERY start, and
        // treated any exception as "already logged in". Here we wait for
        // whichever appears first — the login box OR the table — then check
        // which one it was. Faster, and a real failure now gives a clear error.
        // LEARN: `Locator.Or(...)` = "either of these two".
        await emailBox.Or(table).First.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 20000 });

        if (!await emailBox.IsVisibleAsync())
        {
            AppLog.Info("Already logged in.");
            return;
        }

        var credentials = await GetCredentialsAsync(ct)
            ?? throw new InvalidOperationException("Login was cancelled — no credentials were provided.");

        var passwordBox = page.Locator("[formcontrolname='password']");
        await emailBox.ClickAsync();
        await emailBox.FillAsync(credentials.Email);
        await passwordBox.ClickAsync();
        await passwordBox.FillAsync(credentials.Password);

        var actualEmail = await emailBox.InputValueAsync();
        if (!string.Equals(actualEmail, credentials.Email, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Email field shows '{actualEmail}' after fill() — it did not take the value as expected.");
        }

        await page.Locator("button:has-text('Login')").ClickAsync();

        try
        {
            await page.WaitForURLAsync(
                url => !url.Contains("login", StringComparison.OrdinalIgnoreCase),
                new() { Timeout = 15000 });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _cachedCredentials = null; // do not keep credentials the site just rejected

            var errorText = "";
            try
            {
                errorText = await page.Locator(".alert, .error, [class*='error'], .toast, .mat-error")
                    .First.TextContentAsync(new() { Timeout = 1000 }) ?? "";
            }
            catch
            {
                // Could not read an error message from the page; that is fine.
            }

            var hint = string.IsNullOrWhiteSpace(errorText) ? "" : $" Page shows: {errorText.Trim()}";
            throw new InvalidOperationException(
                $"Login was rejected — still on the login page after submitting credentials.{hint}");
        }

        _cachedCredentials = credentials; // remembered in memory for this run only

        await page.WaitForSelectorAsync("a.sidenav-link", new() { Timeout = 15000 });

        var href = await page.GetByRole(AriaRole.Link, new() { Name = "Booking Table" }).GetAttributeAsync("href");
        if (string.IsNullOrWhiteSpace(href))
            throw new InvalidOperationException("Could not find the Booking Table link after login.");

        // WHY: `BaseUrl + href` breaks if href is a full URL or lacks a leading
        // "/". `new Uri(baseUri, href)` handles every case correctly.
        //
        // ASSUMPTION to verify: this "Booking Table" page and the CacheUrl page
        // (used when already logged in) show the same table and date box. If they
        // differ, extraction depends on HOW you got there.
        var target = new Uri(new Uri(AppConfig.BaseUrl), href);
        await page.GotoAsync(target.ToString());
    }

    // WHY: The original REQUIRED the password in a plain-text .env file. Chrome's
    // saved profile already keeps you logged in, so the password is only needed
    // occasionally. Order of preference:
    //   1. HEALTH_EMAIL / HEALTH_PASSWORD from the environment or .env (optional)
    //   2. a password remembered earlier in this run
    //   3. ask the user with a dialog (held in memory only, never saved)
    private async Task<LoginCredentials?> GetCredentialsAsync(CancellationToken ct)
    {
        var email = Environment.GetEnvironmentVariable("HEALTH_EMAIL")?.Trim();
        var password = Environment.GetEnvironmentVariable("HEALTH_PASSWORD");

        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(password))
            return new LoginCredentials(email, password);

        if (_cachedCredentials is not null)
            return _cachedCredentials;

        // RunContinuationsAsynchronously: when the window completes the promise,
        // our code resumes on a thread-pool thread instead of running inside the
        // window's own call stack.
        var reply = new TaskCompletionSource<LoginCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new CredentialsRequestMsg(reply));

        // If the app is closing while the dialog is open, cancel the wait.
        using var registration = ct.Register(() => reply.TrySetCanceled());
        return await reply.Task;
    }

    // -------------------------------------------------------------------------
    // Chrome / Edge handling
    // -------------------------------------------------------------------------
    // Prefer a tab already on the booking site; otherwise reuse an empty tab
    // (instead of grabbing whatever tab happens to be first, which could be
    // something the user was reading).
    private static IPage? PickPage(IBrowserContext context)
    {
        var bookingPage = context.Pages.FirstOrDefault(p =>
            p.Url.StartsWith(AppConfig.BaseUrl, StringComparison.OrdinalIgnoreCase));
        if (bookingPage is not null)
            return bookingPage;

        return context.Pages.FirstOrDefault(p =>
            p.Url == "about:blank" || p.Url.StartsWith("chrome://newtab", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> CdpIsUpAsync()
    {
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{AppConfig.CdpPort}/json/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false; // nothing is listening (or it timed out): treat as "not up"
        }
    }

    private static string FindBrowserExecutable()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // LEARN: `string?` in the list because GetEnvironmentVariable can return null.
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CHROME_PATH"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            // WHY: Microsoft Edge ships with Windows and is also Chromium, so it
            // works as a fallback. (The original also listed Linux/macOS paths,
            // which are pointless in a Windows-only app.)
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not find Chrome or Edge automatically. Set CHROME_PATH to the full path of the browser .exe.");
    }

    private static async Task LaunchDetachedBrowserAsync(CancellationToken ct)
    {
        var exePath = FindBrowserExecutable();
        Directory.CreateDirectory(AppConfig.ProfileDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false
        };

        // LEARN: ArgumentList adds each argument separately, so paths with
        // spaces need no manual quoting.
        startInfo.ArgumentList.Add($"--remote-debugging-port={AppConfig.CdpPort}");
        startInfo.ArgumentList.Add($"--user-data-dir={AppConfig.ProfileDir}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");

        // `using` releases OUR handle to the process; it does not close the browser.
        using var process = Process.Start(startInfo);

        for (var i = 0; i < 40; i++) // up to ~20 seconds
        {
            if (await CdpIsUpAsync())
                return;
            await Task.Delay(500, ct);
        }

        throw new InvalidOperationException(
            "The browser did not come up with remote debugging enabled within 20 seconds. " +
            "If a Chrome window using the same profile folder is already open, close it first.");
    }

    // -------------------------------------------------------------------------
    // Fatal errors
    // -------------------------------------------------------------------------
    private async Task ReportFatalAsync(Exception ex, IPage? page)
    {
        AppLog.Error($"Browser session crashed: {ex}"); // full detail goes to the log only

        var debugInfo = "";
        try
        {
            if (page is not null)
            {
                debugInfo = $"\n\nCurrent URL: {page.Url}";

                // PRIVACY: a screenshot of the booking table contains patient names
                // and would sit on disk. It is OFF unless you opt in with
                // SAVE_ERROR_SCREENSHOTS=1, and you should delete it afterwards.
                if (Environment.GetEnvironmentVariable("SAVE_ERROR_SCREENSHOTS") == "1")
                {
                    Directory.CreateDirectory(AppConfig.DataDir);
                    var shot = Path.Combine(AppConfig.DataDir, "last_error.png");
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot });
                    debugInfo += $"\nScreenshot (contains patient data!): {shot}";
                }
            }
        }
        catch
        {
            // Collecting debug info is best-effort.
        }

        Post(new FatalMsg(
            $"The browser session stopped:\n{ex.Message}{debugInfo}\n\nFull details are in the log folder:\n{AppConfig.LogDir}"));
    }
}

// =============================================================================
// 7. LOGIN DIALOG
// =============================================================================

// A small pop-up window asking for email and password. Used only when the site
// shows its login page and no credentials were supplied through .env.
internal sealed class LoginDialog : Form
{
    private readonly TextBox _email = new();
    private readonly TextBox _password = new();

    public LoginDialog()
    {
        Text = "Booking site login";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 150);

        var emailLabel = new Label { Text = "Email", Left = 12, Top = 15, Width = 70 };
        _email.SetBounds(90, 12, 215, 23);

        var passwordLabel = new Label { Text = "Password", Left = 12, Top = 50, Width = 70 };
        _password.SetBounds(90, 47, 215, 23);
        _password.UseSystemPasswordChar = true; // shows dots instead of the text

        // LEARN: Setting DialogResult on a button closes the dialog and makes
        // ShowDialog() return that value.
        var ok = new Button { Text = "Log in", DialogResult = DialogResult.OK, Left = 140, Top = 100, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 225, Top = 100, Width = 80 };

        Controls.AddRange(new Control[] { emailLabel, _email, passwordLabel, _password, ok, cancel });
        AcceptButton = ok;    // Enter key = Log in
        CancelButton = cancel; // Esc key  = Cancel
    }

    public LoginCredentials Result => new(_email.Text.Trim(), _password.Text);
}

// =============================================================================
// 8. THE MAIN WINDOW
// =============================================================================

internal sealed class CalendarApp : Form
{
    private readonly TableLayoutPanel _calendar = new();
    private readonly Label _monthLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _textArea = new();
    private readonly Button _previousMonth = new();
    private readonly Button _nextMonth = new();
    private readonly ConcurrentQueue<WorkerMessage> _messages = new();
    private readonly PlaywrightWorker _worker;

    // LEARN: Two different "Timer" classes exist. We want the Windows Forms one,
    // which fires on the window's own thread (so it may safely touch controls).
    private readonly System.Windows.Forms.Timer _queueTimer = new();
    private readonly System.Windows.Forms.Timer _clipboardTimer = new();

    // WHY: We keep our own list of day buttons so we can DISPOSE them. The
    // original only removed them from the panel, which leaves their Windows
    // handles allocated — a slow resource leak each time you changed month.
    private readonly List<Button> _dayButtons = new();

    private DateTime _currentDate = DateTime.Today;
    private Button? _selectedButton;
    private bool _busy;
    private bool _workerStopped; // set after a fatal error; the worker is gone
    private string? _lastCopiedText;

    private static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(60);

    public CalendarApp()
    {
        Text = "Booking Extraction Tool";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 520);
        MinimumSize = new Size(400, 520);

        _worker = new PlaywrightWorker(_messages);

        FormClosing += OnFormClosing;
        SetupUi();
        RenderCalendar();

        SetStatus("Starting up...");
        SetBusy(true);
        _worker.Start();

        // The worker cannot touch the window, so we poll its mailbox 10x/second.
        _queueTimer.Interval = 100;
        _queueTimer.Tick += (_, _) => CheckMessages();
        _queueTimer.Start();

        _clipboardTimer.Interval = (int)ClipboardLifetime.TotalMilliseconds;
        _clipboardTimer.Tick += (_, _) => ClearClipboardIfOurs();
    }

    private void SetupUi()
    {
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 55,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };

        _previousMonth.Text = "<";
        _previousMonth.Width = 40;
        _previousMonth.Click += (_, _) => ChangeMonth(-1);

        _monthLabel.TextAlign = ContentAlignment.MiddleCenter;
        _monthLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _monthLabel.Width = 230;
        _monthLabel.Height = 35;

        _nextMonth.Text = ">";
        _nextMonth.Width = 40;
        _nextMonth.Click += (_, _) => ChangeMonth(+1);

        header.Controls.Add(_previousMonth);
        header.Controls.Add(_monthLabel);
        header.Controls.Add(_nextMonth);

        _calendar.Dock = DockStyle.Top;
        _calendar.Height = 290;
        _calendar.ColumnCount = 7;
        _calendar.RowCount = 7;
        _calendar.Padding = new Padding(8, 5, 8, 5);
        _calendar.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;

        for (var i = 0; i < 7; i++)
            _calendar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));

        for (var i = 0; i < 7; i++)
            _calendar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7));

        var dayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        for (var i = 0; i < dayNames.Length; i++)
        {
            _calendar.Controls.Add(new Label
            {
                Text = dayNames[i],
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            }, i, 0);
        }

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 60;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.AutoSize = false;

        _textArea.Dock = DockStyle.Fill;
        _textArea.Multiline = true;
        _textArea.ReadOnly = true;
        _textArea.ScrollBars = ScrollBars.Vertical;
        _textArea.Font = new Font("Consolas", 9);

        // LEARN: With Dock, the order controls are added matters (later = docked first).
        Controls.Add(_textArea);
        Controls.Add(_statusLabel);
        Controls.Add(_calendar);
        Controls.Add(header);
    }

    private void RenderCalendar()
    {
        // LEARN: SuspendLayout/ResumeLayout stop the panel from re-arranging
        // itself after every single change, which avoids flicker.
        _calendar.SuspendLayout();
        try
        {
            foreach (var old in _dayButtons)
            {
                _calendar.Controls.Remove(old);
                old.Dispose(); // LEARN: Remove() alone does NOT free a control's resources.
            }

            _dayButtons.Clear();
            _selectedButton = null;
            _monthLabel.Text = _currentDate.ToString("MMMM yyyy");

            var year = _currentDate.Year;
            var month = _currentDate.Month;
            var firstOfMonth = new DateTime(year, month, 1);
            var daysInMonth = DateTime.DaysInMonth(year, month);
            var offset = ((int)firstOfMonth.DayOfWeek + 6) % 7; // shifts Sunday-first to Monday-first

            for (var day = 1; day <= daysInMonth; day++)
            {
                var index = offset + day - 1;
                var row = (index / 7) + 1; // row 0 holds the weekday names
                var col = index % 7;

                // LEARN: Each loop pass declares its OWN `date`, so the click
                // handler below "captures" that pass's value (a closure).
                var date = new DateTime(year, month, day);
                var button = new Button
                {
                    Text = day.ToString(),
                    Dock = DockStyle.Fill,
                    Margin = new Padding(2),
                    Tag = date
                };

                if (date == DateTime.Today)
                {
                    button.Text = $"✓ {day}";
                    button.BackColor = Color.LightBlue;
                }

                button.Click += (_, _) => OnDateClick(date, button);
                _dayButtons.Add(button);
                _calendar.Controls.Add(button, col, row);
            }
        }
        finally
        {
            _calendar.ResumeLayout();
        }

        UpdateControlsEnabled();
    }

    private void ChangeMonth(int delta)
    {
        if (_busy || _workerStopped)
            return;

        _currentDate = _currentDate.AddMonths(delta);
        RenderCalendar();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateControlsEnabled();
    }

    // One place decides whether the controls are clickable, so the "busy" and
    // "worker stopped" rules can never drift apart.
    private void UpdateControlsEnabled()
    {
        var enabled = !_busy && !_workerStopped;
        _previousMonth.Enabled = enabled;
        _nextMonth.Enabled = enabled;

        foreach (var button in _dayButtons)
            button.Enabled = enabled;
    }

    private void OnDateClick(DateTime date, Button clickedButton)
    {
        if (_busy || _workerStopped)
            return;

        if (_selectedButton?.Tag is DateTime previous)
            _selectedButton.BackColor = previous == DateTime.Today ? Color.LightBlue : SystemColors.Control;

        clickedButton.BackColor = Color.LightGreen;
        _selectedButton = clickedButton;

        // WHY (bug fix): in a custom format, "/" means "the PC's date separator",
        // NOT a literal slash. On a PC set to another region this could become
        // "01.11.2026". InvariantCulture guarantees real slashes.
        var targetDate = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        SetStatus($"Fetching data for {targetDate}...");
        SetBusy(true);
        _worker.SubmitDate(targetDate);
    }

    // Runs 10x per second on the window's thread: handles everything the worker posted.
    private void CheckMessages()
    {
        while (_messages.TryDequeue(out var message))
            ProcessMessage(message);
    }

    private void ProcessMessage(WorkerMessage message)
    {
        // LEARN: A `switch` on a TYPE (pattern matching). Each `case` tests the
        // message's type and, if it matches, gives it a name to use.
        switch (message)
        {
            case StatusMsg status:
                SetStatus(status.Text);
                break;

            case ReadyMsg:
                SetBusy(false);
                SetStatus("Ready. Select a date to fetch bookings.");
                break;

            case CredentialsRequestMsg request:
                AskForCredentials(request);
                break;

            case FetchedMsg fetched:
                ShowFetched(fetched);
                break;

            case ErrorMsg error:
                SetBusy(false);
                SetStatus("Error occurred — see dialog.");
                MessageBox.Show(error.Summary, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;

            case FatalMsg fatal:
                // WHY (bug fix): the original re-enabled the calendar after a fatal
                // error even though the worker had stopped, so the next click
                // queued a job nobody would ever read and the app showed
                // "Fetching..." forever. Now the controls stay disabled.
                _workerStopped = true;
                SetBusy(false);
                SetStatus("Browser session stopped — close and reopen the app.");
                MessageBox.Show(fatal.Summary, "Fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
        }
    }

    private void AskForCredentials(CredentialsRequestMsg request)
    {
        // Pause polling while the modal dialog is open, so messages are not
        // processed in the middle of it.
        _queueTimer.Stop();
        try
        {
            using var dialog = new LoginDialog();
            LoginCredentials? credentials = null;

            if (dialog.ShowDialog(this) == DialogResult.OK &&
                dialog.Result is { Email.Length: > 0, Password.Length: > 0 } result)
            {
                credentials = result;
            }

            // Wakes up the worker, which is `await`ing this promise.
            request.Reply.TrySetResult(credentials);
        }
        finally
        {
            _queueTimer.Start();
        }
    }

    private void ShowFetched(FetchedMsg fetched)
    {
        SetBusy(false);

        var status = $"Found {fetched.Bookings.Count} patients for {fetched.Date} (column '{fetched.ColumnName}').";

        // WHY (bug fix): Clipboard.SetText("") throws an exception, so an empty
        // day produced a confusing "clipboard error" in the original.
        if (fetched.Bookings.Count > 0)
        {
            if (CopyToClipboard(string.Join('\t', fetched.Bookings)))
                status += " Copied to clipboard (auto-cleared in 60 s).";
        }
        else
        {
            status += " Nothing copied.";
        }

        if (fetched.Warnings.Count > 0)
            status += " WARNING — see details below.";

        SetStatus(status);

        var text = new StringBuilder();
        foreach (var warning in fetched.Warnings)
            text.AppendLine("WARNING: " + warning);

        if (fetched.Warnings.Count > 0)
            text.AppendLine();

        text.Append(fetched.Bookings.Count == 0
            ? "No bookings found."
            : string.Join(Environment.NewLine, fetched.Bookings));

        _textArea.Text = text.ToString();
    }

    // PRIVACY: patient names on the clipboard can be captured by Windows
    // clipboard history (Win+V) and cloud clipboard sync. The special format
    // below asks Windows NOT to record this item, and a timer clears it later.
    // Best effort: test with Win+V on your PC. Returns true if text was copied.
    private bool CopyToClipboard(string text)
    {
        try
        {
            try
            {
                var data = new DataObject();
                data.SetText(text);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, copy: true);
            }
            catch (Exception ex) when (ex is not System.Runtime.InteropServices.ExternalException)
            {
                // The "exclude from history" extra failed; fall back to plain text.
                Clipboard.SetText(text);
            }

            _lastCopiedText = text;
            _clipboardTimer.Stop();
            _clipboardTimer.Start(); // restart the 60-second countdown
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Clipboard copy failed: {ex.Message}");
            MessageBox.Show($"Could not copy the results to the clipboard:\n{ex.Message}",
                "Clipboard error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    // Only clears the clipboard if it STILL holds what we put there; if you have
    // copied something else since, we leave it alone.
    private void ClearClipboardIfOurs()
    {
        _clipboardTimer.Stop();

        try
        {
            if (_lastCopiedText is not null && Clipboard.ContainsText() && Clipboard.GetText() == _lastCopiedText)
                Clipboard.Clear();
        }
        catch
        {
            // The clipboard can be briefly locked by another program; ignore.
        }

        _lastCopiedText = null;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _queueTimer.Stop();
        _clipboardTimer.Stop();
        ClearClipboardIfOurs();

        _worker.Shutdown();
        _worker.WaitForExit(TimeSpan.FromSeconds(3));
        // Chrome is deliberately left running: it is its own program.
    }
}

// =============================================================================
// 9. PROGRAM ENTRY POINT
// =============================================================================

internal static class Program
{
    // LEARN: [STAThread] is required by Windows Forms (clipboard, dialogs).
    [STAThread]
    private static void Main()
    {
        LoadDotEnvIfPresent();

        // WHY: The original had no safety net, so an unexpected error killed the
        // app silently. These handlers log it and show a message instead.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnexpected(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error($"Unhandled exception: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        Application.Run(new CalendarApp());
    }

    private static void ReportUnexpected(Exception ex)
    {
        AppLog.Error($"Unexpected error: {ex}");
        MessageBox.Show($"An unexpected error occurred:\n{ex.Message}\n\nDetails were written to:\n{AppConfig.LogDir}",
            "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // The .env file is OPTIONAL now. First one found wins.
    private static void LoadDotEnvIfPresent()
    {
        var candidates = new[]
        {
            Path.Combine(AppConfig.DataDir, ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env")
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
            DotEnv.Load(found);
    }
}
