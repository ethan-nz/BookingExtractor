using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BookingExtractor;

internal static class AppConfig
{
    public const string BaseUrl = "https://booking.tianlan.co.nz";
    public const string CacheUrl = "https://booking.tianlan.co.nz/booking/cache-table";
    public const string MyColumnName = "Gao";
    public const int CdpPort = 9222;
    public const int ColumnOffset = -1;

    public static string ProfileDir => Path.Combine(AppContext.BaseDirectory, "playwright_profile");
}

internal sealed record WorkerResult(
    string? Status = null,
    bool Ready = false,
    bool Fatal = false,
    string? Error = null,
    string? Date = null,
    List<string>? Bookings = null,
    int Skipped = 0,
    bool Mismatch = false
);

internal sealed record WorkerJob(string Kind, string? Payload);

internal static class DotEnv
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            // Approximate python-dotenv's unquoted-comment behavior used by the
            // original script: a literal '#' in an unquoted value starts a comment.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                value = value[1..^1];
            else
            {
                var comment = value.IndexOf('#');
                if (comment >= 0)
                    value = value[..comment].TrimEnd();
            }

            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal sealed class PlaywrightWorker
{
    private readonly BlockingCollection<WorkerJob> _jobs = new();
    private readonly ConcurrentQueue<WorkerResult> _results;
    private readonly Thread _thread;

    public PlaywrightWorker(ConcurrentQueue<WorkerResult> results)
    {
        _results = results;
        _thread = new Thread(Run) { IsBackground = true, Name = "PlaywrightWorker" };
    }

    public void Start() => _thread.Start();

    public void SubmitDate(string date)
    {
        if (!_jobs.IsAddingCompleted)
            _jobs.Add(new WorkerJob("fetch", date));
    }

    public void Shutdown()
    {
        try
        {
            _jobs.Add(new WorkerJob("shutdown", null));
        }
        catch (InvalidOperationException)
        {
            // Already completed.
        }
    }

    private static bool IsTargetClosed(Exception ex) =>
        ex.GetType().Name.Equals("TargetClosedError", StringComparison.OrdinalIgnoreCase) ||
        ex.GetType().Name.Equals("TargetClosedException", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase);

    private void Run()
    {
        string email;
        string password;

        try
        {
            email = Environment.GetEnvironmentVariable("HEALTH_EMAIL")?.Trim() ?? "";
            password = Environment.GetEnvironmentVariable("HEALTH_PASSWORD") ?? "";
        }
        catch (Exception ex)
        {
            _results.Enqueue(new WorkerResult(Fatal: true,
                Error: $"Could not read credentials from environment variables: {ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            _results.Enqueue(new WorkerResult(
                Fatal: true,
                Error: "Missing required environment variable HEALTH_EMAIL or HEALTH_PASSWORD. " +
                       "Set them in a .env file next to the executable or in Windows environment variables."));
            return;
        }

        var firstAttempt = true;

        while (true)
        {
            IPage? page = null;
            var shutdownRequested = false;

            try
            {
                var playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
                try
                {
                    if (!CdpIsUp(AppConfig.CdpPort))
                    {
                        _results.Enqueue(new WorkerResult(
                            Status: firstAttempt ? "Launching Chrome (first run)..." : "Chrome window is gone — launching a new one..."));
                        LaunchDetachedChrome();
                    }
                    else
                    {
                        _results.Enqueue(new WorkerResult(Status: "Reconnecting to open Chrome window..."));
                    }

                    firstAttempt = false;

                    var browser = playwright.Chromium
                        .ConnectOverCDPAsync($"http://127.0.0.1:{AppConfig.CdpPort}")
                        .GetAwaiter().GetResult();

                    var context = browser.Contexts.FirstOrDefault();
                    if (context == null)
                        context = browser.NewContextAsync().GetAwaiter().GetResult();

                    page = context.Pages.FirstOrDefault(p =>
                        p.Url.StartsWith(AppConfig.BaseUrl, StringComparison.OrdinalIgnoreCase));

                    if (page == null)
                        page = context.Pages.FirstOrDefault() ?? context.NewPageAsync().GetAwaiter().GetResult();

                    LoginIfNeeded(page, email, password);
                    page.WaitForSelectorAsync("table", new() { Timeout = 25000 }).GetAwaiter().GetResult();

                    _results.Enqueue(new WorkerResult(Ready: true));

                    while (true)
                    {
                        var job = _jobs.Take();

                        if (job.Kind == "shutdown")
                        {
                            shutdownRequested = true;
                            break;
                        }

                        if (job.Kind != "fetch" || job.Payload == null)
                            continue;

                        try
                        {
                            var result = FetchForDate(page, job.Payload);
                            _results.Enqueue(new WorkerResult(
                                Date: job.Payload,
                                Bookings: result.Bookings,
                                Skipped: result.Skipped,
                                Mismatch: result.Mismatch));
                        }
                        catch (Exception fetchEx)
                        {
                            if (IsTargetClosed(fetchEx))
                            {
                                _results.Enqueue(new WorkerResult(
                                    Status: "Browser connection was lost (window closed, or Chrome discarded the idle tab) — reconnecting..."));
                                break;
                            }

                            _results.Enqueue(new WorkerResult(
                                Error: $"Failed to fetch {job.Payload}:\n{fetchEx}"));
                        }
                    }
                }
                finally
                {
                    // Important: do not close the browser/context here. Chrome was
                    // launched as an independent OS process and should survive us.
                    playwright.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (IsTargetClosed(ex))
                {
                    _results.Enqueue(new WorkerResult(Status: "Browser connection was lost — reconnecting..."));
                    Thread.Sleep(1000);
                    continue;
                }

                var debugInfo = "";
                try
                {
                    if (page != null)
                    {
                        var url = page.Url;
                        var title = page.TitleAsync().GetAwaiter().GetResult();
                        var screenshotPath = Path.Combine(AppConfig.ProfileDir, "..", "last_error.png");
                        page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.GetFullPath(screenshotPath) })
                            .GetAwaiter().GetResult();
                        debugInfo = $"\n\nCurrent URL: {url}\nPage title: {title}\nScreenshot saved to: {Path.GetFullPath(screenshotPath)}";
                    }
                }
                catch
                {
                    // Ignore debug collection failures, matching the Python behavior.
                }

                _results.Enqueue(new WorkerResult(
                    Fatal: true,
                    Error: $"Browser session crashed:\n{ex}{debugInfo}"));
                return;
            }

            if (shutdownRequested)
                return;

            Thread.Sleep(1000);
        }
    }

    private static bool CdpIsUp(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = client.GetAsync($"http://127.0.0.1:{port}/json/version")
                .GetAwaiter().GetResult();
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private static string FindChromeExecutable()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CHROME_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            "/usr/bin/google-chrome",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not find a Chrome executable automatically. Set CHROME_PATH to the full path of chrome.exe.");
    }

    private static void LaunchDetachedChrome()
    {
        var chromePath = FindChromeExecutable();
        Directory.CreateDirectory(AppConfig.ProfileDir);

        var psi = new ProcessStartInfo
        {
            FileName = chromePath,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        psi.ArgumentList.Add($"--remote-debugging-port={AppConfig.CdpPort}");
        psi.ArgumentList.Add($"--user-data-dir={AppConfig.ProfileDir}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");

        Process.Start(psi);

        for (var i = 0; i < 40; i++)
        {
            if (CdpIsUp(AppConfig.CdpPort))
                return;
            Thread.Sleep(500);
        }

        throw new InvalidOperationException(
            "Chrome did not come up with remote debugging enabled within 20 seconds. " +
            "If a Chrome window using the same playwright_profile folder was already open, close it first.");
    }

    private static void LoginIfNeeded(IPage page, string email, string password)
    {
        page.GotoAsync(AppConfig.CacheUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded })
            .GetAwaiter().GetResult();

        var emailBox = page.Locator("[formcontrolname='email']");
        var needsLogin = false;

        try
        {
            emailBox.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8000 })
                .GetAwaiter().GetResult();
            needsLogin = true;
        }
        catch
        {
            needsLogin = false;
        }

        if (!needsLogin)
            return;

        var passwordBox = page.Locator("[formcontrolname='password']");
        emailBox.ClickAsync().GetAwaiter().GetResult();
        emailBox.FillAsync(email).GetAwaiter().GetResult();
        passwordBox.ClickAsync().GetAwaiter().GetResult();
        passwordBox.FillAsync(password).GetAwaiter().GetResult();

        var actualEmail = emailBox.InputValueAsync().GetAwaiter().GetResult();
        if (!string.Equals(actualEmail, email, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Email field shows '{actualEmail}' after fill() — it did not take the value as expected.");
        }

        page.Locator("button:has-text('Login')").ClickAsync().GetAwaiter().GetResult();

        try
        {
            page.WaitForURLAsync(url => !url.Contains("login", StringComparison.OrdinalIgnoreCase),
                    new() { Timeout = 15000 })
                .GetAwaiter().GetResult();
        }
        catch
        {
            var errorText = "";
            try
            {
                errorText = page.Locator(".alert, .error, [class*='error'], .toast, .mat-error")
                    .First.TextContentAsync(new() { Timeout = 1000 })
                    .GetAwaiter().GetResult() ?? "";
            }
            catch
            {
                // Ignore lookup failure.
            }

            var hint = string.IsNullOrWhiteSpace(errorText) ? "" : $" Page shows: {errorText.Trim()}";
            throw new InvalidOperationException(
                $"Login was rejected — still on the login page after submitting credentials.{hint}");
        }

        page.WaitForSelectorAsync("a.sidenav-link", new() { Timeout = 15000 })
            .GetAwaiter().GetResult();

        var bookingTableHref = page.GetByRole(AriaRole.Link, new() { Name = "Booking Table" })
            .GetAttributeAsync("href")
            .GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(bookingTableHref))
            throw new InvalidOperationException("Could not find the Booking Table link after login.");

        page.GotoAsync(AppConfig.BaseUrl + bookingTableHref)
            .GetAwaiter().GetResult();
    }

    private static (List<string> Bookings, int Skipped, bool Mismatch) FetchForDate(IPage page, string date)
    {
        var dateInput = page.Locator("input[name='date']");
        var responseMatched = false;

        try
        {
            page.RunAndWaitForResponseAsync(async () =>
            {
                await dateInput.FillAsync(date);
                await dateInput.DispatchEventAsync("input");
                await dateInput.DispatchEventAsync("change");
            }, response =>
                response.Url.Contains("booking", StringComparison.OrdinalIgnoreCase) && response.Status == 200,
            new() { Timeout = 10000 }).GetAwaiter().GetResult();
            responseMatched = true;
        }
        catch
        {
            // Match Python fallback behavior: wait briefly when the expected XHR
            // pattern is not observed.
            page.WaitForTimeoutAsync(1500).GetAwaiter().GetResult();
        }

        var actualValue = dateInput.InputValueAsync().GetAwaiter().GetResult();
        if (!string.Equals(actualValue, date, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Date field shows '{actualValue}' instead of '{date}' after filling it. " +
                "The site's date field likely isn't a plain text input and may require its own calendar/month-navigation UI.");
        }

        page.WaitForSelectorAsync("tbody tr", new() { Timeout = 10000 })
            .GetAwaiter().GetResult();

        var headers = page.Locator("thead th").AllTextContentsAsync().GetAwaiter().GetResult()
            .Select(h => h.Trim()).ToList();

        if (!headers.Contains(AppConfig.MyColumnName, StringComparer.Ordinal))
            throw new InvalidOperationException($"Column '{AppConfig.MyColumnName}' not found in headers: {string.Join(", ", headers)}");

        var colIndex = headers.FindIndex(h => string.Equals(h, AppConfig.MyColumnName, StringComparison.Ordinal)) + AppConfig.ColumnOffset;
        var rows = page.Locator("tbody tr").AllAsync().GetAwaiter().GetResult();

        var buttonSelector = "button.btn-outline-success, button.btn-outline-primary, button.btn-outline-secondary";
        var expectedTotal = 0;

        foreach (var row in rows)
        {
            var cells = row.Locator("td").AllAsync().GetAwaiter().GetResult();
            if (colIndex < 0 || colIndex >= cells.Count)
                continue;

            expectedTotal += cells[colIndex].Locator(buttonSelector).CountAsync().GetAwaiter().GetResult();
        }

        var bookings = new List<string>();
        var skipped = 0;

        foreach (var row in rows)
        {
            var cells = row.Locator("td").AllAsync().GetAwaiter().GetResult();
            if (colIndex < 0 || colIndex >= cells.Count)
                continue;

            var validButtons = cells[colIndex].Locator(buttonSelector).AllAsync().GetAwaiter().GetResult();
            foreach (var button in validButtons)
            {
                string rawText;
                try
                {
                    rawText = button.TextContentAsync(new() { Timeout = 3000 })
                        .GetAwaiter().GetResult() ?? "";
                }
                catch
                {
                    skipped++;
                    continue;
                }

                var npSuffix = rawText.Contains("np", StringComparison.OrdinalIgnoreCase) ? " @" : "";
                var cleanName = Regex.Replace(rawText.Trim(), @"\d{2}/\d{2}/\d{4}", "");
                var firstLine = cleanName.Split('\n')[0].Trim();
                cleanName = CultureTitleCase(firstLine) + npSuffix;
                bookings.Add(cleanName);
            }
        }

        var mismatch = bookings.Count + skipped != expectedTotal;
        _ = responseMatched; // retained to document that the response was optional.
        return (bookings, skipped, mismatch);
    }

    private static string CultureTitleCase(string value)
    {
        if (value.Length == 0)
            return value;

        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }
}

internal sealed class CalendarApp : Form
{
    private readonly TableLayoutPanel _calendar = new();
    private readonly Label _monthLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _textArea = new();
    private readonly Button _previousMonth = new();
    private readonly Button _nextMonth = new();
    private readonly ConcurrentQueue<WorkerResult> _results = new();
    private readonly PlaywrightWorker _worker;
    private readonly System.Windows.Forms.Timer _queueTimer = new();

    private DateTime _currentDate = DateTime.Today;
    private Button? _selectedButton;
    private bool _busy;

    public CalendarApp()
    {
        Text = "Booking Extraction Tool";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 520);
        MinimumSize = new Size(400, 520);

        _worker = new PlaywrightWorker(_results);

        FormClosing += OnFormClosing;
        SetupUi();
        RenderCalendar();

        SetStatus("Starting up...");
        SetBusy(true);
        _worker.Start();

        _queueTimer.Interval = 100;
        _queueTimer.Tick += (_, _) => CheckQueue();
        _queueTimer.Start();
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
        _previousMonth.Click += (_, _) => PreviousMonth();

        _monthLabel.TextAlign = ContentAlignment.MiddleCenter;
        _monthLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _monthLabel.Width = 230;
        _monthLabel.Height = 35;

        _nextMonth.Text = ">";
        _nextMonth.Width = 40;
        _nextMonth.Click += (_, _) => NextMonth();

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
        {
            _calendar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
        }

        for (var i = 0; i < 7; i++)
        {
            _calendar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7));
        }

        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        for (var i = 0; i < days.Length; i++)
        {
            var label = new Label
            {
                Text = days[i],
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _calendar.Controls.Add(label, i, 0);
        }

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 60;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.AutoEllipsis = false;
        _statusLabel.AutoSize = false;

        _textArea.Dock = DockStyle.Fill;
        _textArea.Multiline = true;
        _textArea.ReadOnly = true;
        _textArea.ScrollBars = ScrollBars.Vertical;
        _textArea.Font = new Font("Consolas", 9);
        _textArea.Margin = new Padding(10);

        Controls.Add(_textArea);
        Controls.Add(_statusLabel);
        Controls.Add(_calendar);
        Controls.Add(header);
    }

    private void RenderCalendar()
    {
        while (_calendar.Controls.Count > 7)
            _calendar.Controls.RemoveAt(_calendar.Controls.Count - 1);

        _selectedButton = null;
        _monthLabel.Text = _currentDate.ToString("MMMM yyyy");

        var year = _currentDate.Year;
        var month = _currentDate.Month;
        var first = new DateTime(year, month, 1);
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var offset = ((int)first.DayOfWeek + 6) % 7; // Monday = 0

        for (var day = 1; day <= daysInMonth; day++)
        {
            var index = offset + day - 1;
            var row = (index / 7) + 1;
            var col = index % 7;
            var button = new Button
            {
                Text = day.ToString(),
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                Tag = new DateTime(year, month, day)
            };

            var date = (DateTime)button.Tag;
            if (date == DateTime.Today)
            {
                button.Text = $"✓ {day}";
                button.BackColor = Color.LightBlue;
                button.FlatStyle = FlatStyle.Standard;
            }

            button.Click += (_, _) => OnDateClick(date, button);
            button.Enabled = !_busy;
            _calendar.Controls.Add(button, col, row);
        }
    }

    private void PreviousMonth()
    {
        if (_busy)
            return;

        _currentDate = _currentDate.AddMonths(-1);
        RenderCalendar();
    }

    private void NextMonth()
    {
        if (_busy)
            return;

        _currentDate = _currentDate.AddMonths(1);
        RenderCalendar();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _previousMonth.Enabled = !busy;
        _nextMonth.Enabled = !busy;

        foreach (Control control in _calendar.Controls)
        {
            if (control is Button button)
                button.Enabled = !busy;
        }
    }

    private void OnDateClick(DateTime date, Button clickedButton)
    {
        if (_busy)
            return;

        if (_selectedButton?.Tag is DateTime selectedDate)
        {
            _selectedButton.BackColor = selectedDate == DateTime.Today ? Color.LightBlue : SystemColors.Control;
        }

        clickedButton.BackColor = Color.LightGreen;
        _selectedButton = clickedButton;

        var targetDate = date.ToString("dd/MM/yyyy");
        SetStatus($"Fetching data for {targetDate}...");
        SetBusy(true);
        _worker.SubmitDate(targetDate);
    }

    private void CheckQueue()
    {
        while (_results.TryDequeue(out var result))
            ProcessResult(result);
    }

    private void ProcessResult(WorkerResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Status))
        {
            SetStatus(result.Status);
            return;
        }

        if (result.Ready)
        {
            SetBusy(false);
            SetStatus("Ready. Select a date to fetch bookings.");
            return;
        }

        if (result.Fatal)
        {
            SetBusy(false);
            SetStatus("Browser session error — see dialog.");
            MessageBox.Show(result.Error ?? "Unknown fatal error.", "Fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetBusy(false);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            SetStatus("Error occurred — see dialog.");
            MessageBox.Show(result.Error, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var bookings = result.Bookings ?? new List<string>();
        try
        {
            Clipboard.SetText(string.Join('\t', bookings));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy the results to the clipboard:\n{ex.Message}",
                "Clipboard error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var status = $"Found {bookings.Count} patients for {result.Date}. Copied to clipboard!";
        if (result.Skipped > 0)
            status += $" ({result.Skipped} cell(s) skipped — took too long to render.)";
        if (result.Mismatch)
            status += " WARNING: extracted count doesn't match expected — list may be incomplete.";

        SetStatus(status);
        _textArea.Text = bookings.Count == 0 ? "No bookings found." : string.Join(Environment.NewLine, bookings);
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _queueTimer.Stop();
        _worker.Shutdown();
        // Intentionally do not kill Chrome. It belongs to its own OS process.
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Match the Python version's ".env next to the script" behavior while
        // also making `dotnet run` convenient (its executable normally lives
        // under bin\Debug\net8.0-windows).
        var baseEnv = Path.Combine(AppContext.BaseDirectory, ".env");
        var workingEnv = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        DotEnv.Load(File.Exists(baseEnv) ? baseEnv : workingEnv);

        ApplicationConfiguration.Initialize();
        Application.Run(new CalendarApp());
    }
}
