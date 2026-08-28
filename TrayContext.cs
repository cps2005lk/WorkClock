using System.Drawing;
using System.Windows.Forms;

namespace WorkClock;

/// <summary>
/// Drives the system tray icon: shows current attendance status in the
/// tooltip, refreshes on a timer, and lets you manually refresh or
/// re-login from the right-click menu.
/// </summary>
public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly SessionManager _sessionManager;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _reminderTimer;
    private readonly AppConfig _config;

    // Tracks whether the last known status was "Out" so we only show the
    // balloon on the transition into Out, not on every refresh.
    private bool _wasOut;

    // Reminder state: the timestamp of the most recent "in" punch, whether
    // we're currently in, and how many 2-hour reminders we've already fired
    // for this "in" (reset to 0 whenever a new "in" punch appears).
    private DateTime? _lastInTime;
    private bool _currentlyIn;
    private int _remindersFired;
    private DateTime? _lastReminderShown;

    // One reminder window per monitor (so it can't be missed on a multi-monitor
    // setup); dismissing any one closes them all.
    private readonly List<ReminderForm> _reminderForms = new();
    private bool _dismissingReminders;

    // The "Time" of the second IN in the most recent IN-with-no-OUT pair we've
    // already warned about, so we don't warn again for the same occurrence.
    private string? _consecInWarnedKey;

    private static string TodayDate => DateTime.Today.ToString("dd/MM/yyyy");

    public TrayContext(AppConfig config)
    {
        _config = config;
        _sessionManager = new SessionManager(
            config.SessionFile, config.BaseUrl, config.LoginTimeoutMinutes);

        var interval = Math.Max(1, _config.ReminderIntervalHours);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Log in again", null, async (_, _) => await LoginAndRefreshAsync());
        menu.Items.Add(new ToolStripSeparator());

        // Feature switch: turn the "checkpoint" reminder on/off at runtime.
        var reminderToggle = new ToolStripMenuItem($"Remind me every {interval}h while In")
        {
            CheckOnClick = true,
            Checked = _config.RemindersEnabled
        };
        reminderToggle.CheckedChanged += (_, _) =>
        {
            _config.RemindersEnabled = reminderToggle.Checked;
            _config.Save(); // persist the choice across restarts
        };
        menu.Items.Add(reminderToggle);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = CreateStatusIcon(""),
            Text = "WorkClock - starting...",
            ContextMenuStrip = menu,
            Visible = true
        };

        // Refresh on the configured interval.
        _timer = new System.Windows.Forms.Timer { Interval = _config.RefreshIntervalMinutes * 60 * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // Check the "been in for a while" reminder once a minute.
        _reminderTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        _reminderTimer.Tick += (_, _) => CheckReminder();
        _reminderTimer.Start();

        // Kick off the first load.
        _ = InitialLoadAsync();
    }

    private async Task InitialLoadAsync()
    {
        if (!_sessionManager.HasSavedSession())
        {
            await LoginAndRefreshAsync();
        }
        else
        {
            await RefreshAsync();
        }
    }

    private async Task LoginAndRefreshAsync()
    {
        _trayIcon.Text = "WorkClock - logging in...";
        try
        {
            await _sessionManager.LoginAsync();
        }
        catch (Exception ex)
        {
            _trayIcon.Text = Truncate($"Login failed: {ex.Message}");
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var client = _sessionManager.CreateAuthenticatedClient();
            var attendanceClient = new AttendanceClient(client);
            var (success, rawJson) = await attendanceClient.GetAttendanceAsync(_config.EmployeeId, TodayDate);

            if (!success)
            {
                // Session likely expired - log in again automatically.
                await LoginAndRefreshAsync();
                return;
            }

            var summary = AttendanceParser.Parse(rawJson);

            // Current in/out is the Mode ("in"/"out") of the most recent
            // attendance entry. The "~ ..." text on the location row is the
            // location name, not the in/out status.
            var lastEntry = summary.Entries.Count > 0 ? summary.Entries[^1] : null;
            var mode = lastEntry?.Mode?.Trim() ?? "";
            var location = string.IsNullOrWhiteSpace(summary.Status) ? "" : summary.Status;

            string display;
            if (lastEntry is null)
            {
                display = string.IsNullOrWhiteSpace(location) ? "No status available" : location;
            }
            else
            {
                var inOut = IsIn(mode) ? "In" : IsOut(mode) ? "Out" : mode;
                var totalTime = string.IsNullOrWhiteSpace(summary.TotalTimeInside)
                    ? "" : $" (total inside {summary.TotalTimeInside})";
                display = $"{inOut} - {location}{totalTime}".Trim();
            }

            _trayIcon.Text = Truncate(display);
            _trayIcon.Icon = CreateStatusIcon(mode);

            // Track reminder state: find the most recent "in" punch and, if it
            // is newer than the one we knew about, reset the reminder schedule.
            _currentlyIn = IsIn(mode);
            var lastIn = summary.Entries.LastOrDefault(e => IsIn(e.Mode));
            if (lastIn is not null && TryParseEntryTime(lastIn, out var lastInAt) && lastInAt != _lastInTime)
            {
                _lastInTime = lastInAt;
                _remindersFired = 0;
                _lastReminderShown = null;
            }

            // Detect the actual bug in the data: two INs with no OUT between
            // them means the server only counts time after the second IN, so
            // the earlier stretch is already at risk. Warn once per occurrence.
            if (_config.RemindersEnabled)
            {
                var pair = FindLatestConsecutiveIn(summary.Entries);
                if (pair is not null && pair.Value.Second.Time != _consecInWarnedKey)
                {
                    _consecInWarnedKey = pair.Value.Second.Time;
                    ShowReminder(
                        $"Heads up: two 'IN' punches with no 'OUT' between them " +
                        $"({pair.Value.First.Time} then {pair.Value.Second.Time}).\n\n" +
                        $"The server only counts time after {pair.Value.Second.Time}, so the earlier " +
                        $"stretch may be lost. Add a corrective punch if you can.");
                }
            }

            // Show a balloon when we transition into an "Out" status.
            var isOut = IsOut(mode);
            if (isOut && !_wasOut)
            {
                _trayIcon.ShowBalloonTip(
                    _config.BalloonDurationSeconds * 1000,
                    "WorkClock",
                    $"You are currently Out{(string.IsNullOrEmpty(location) ? "" : $" - {location}")}.",
                    ToolTipIcon.Warning);
            }
            _wasOut = isOut;
        }
        catch (Exception ex)
        {
            _trayIcon.Text = Truncate($"Error: {ex.Message}");
        }
    }

    // Reminder cadence while still clocked in: the FIRST reminder fires once
    // you've been in for ReminderIntervalHours. After that it repeats every
    // ReminderFollowupMinutes (a shorter nag) so a missed reminder is caught
    // sooner. A new "in" punch resets the whole cycle.
    private void CheckReminder()
    {
        if (!_config.RemindersEnabled) return;
        if (_lastInTime is null || !_currentlyIn) return;

        var now = DateTime.Now;
        var firstDue = _lastInTime.Value.AddHours(Math.Max(1, _config.ReminderIntervalHours));
        if (now < firstDue) return; // not time for the first reminder yet

        bool due;
        if (_remindersFired == 0)
        {
            due = true; // the first reminder
        }
        else
        {
            var followup = TimeSpan.FromMinutes(Math.Max(1, _config.ReminderFollowupMinutes));
            due = _lastReminderShown is null || now - _lastReminderShown.Value >= followup;
        }

        if (due)
        {
            _remindersFired++;
            _lastReminderShown = now;
            var open = now - _lastInTime.Value;

            // First reminder is plain; follow-ups are flagged as repeats so
            // you can tell at a glance you haven't acted on it yet.
            var header = _remindersFired == 1
                ? ""
                : $"⚠ Still In - reminder #{_remindersFired}\n\n";

            ShowReminder(
                $"{header}" +
                $"You've been clocked In for {FormatDuration(open)} " +
                $"(since {_lastInTime:HH:mm}) with no OUT.\n\n" +
                $"The server only counts time after your most recent IN, so up to this " +
                $"much could be lost if another IN is recorded.\n\n" +
                $"Do a dummy OUT + IN now to lock it in.");
        }
    }

    // "2h 05m" style duration for the reminder message.
    private static string FormatDuration(TimeSpan t) =>
        $"{(int)t.TotalHours}h {t.Minutes:00}m";

    // Scans chronological entries for the most recent adjacent IN,IN pair
    // (an IN directly followed by another IN, with no OUT between them).
    private static (AttendanceEntry First, AttendanceEntry Second)? FindLatestConsecutiveIn(
        IReadOnlyList<AttendanceEntry> entries)
    {
        (AttendanceEntry, AttendanceEntry)? found = null;
        for (var i = 1; i < entries.Count; i++)
        {
            if (IsIn(entries[i - 1].Mode) && IsIn(entries[i].Mode))
                found = (entries[i - 1], entries[i]);
        }
        return found;
    }

    private void ShowReminder(string message)
    {
        // Already showing? Bring every copy forward instead of stacking more.
        if (_reminderForms.Count > 0)
        {
            foreach (var f in _reminderForms)
                if (!f.IsDisposed) f.Activate();
            return;
        }

        // Audible alert - you won't always be looking at the right monitor.
        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }

        // Pop a copy on every monitor; dismissing any one closes them all.
        foreach (var screen in Screen.AllScreens)
        {
            var form = new ReminderForm(message, screen.WorkingArea);
            form.FormClosed += (_, _) => DismissAllReminders();
            _reminderForms.Add(form);
            form.Show();
        }
    }

    private void DismissAllReminders()
    {
        if (_dismissingReminders) return; // avoid re-entry while closing
        _dismissingReminders = true;
        foreach (var f in _reminderForms.ToArray())
            if (!f.IsDisposed) f.Close();
        _reminderForms.Clear();
        _dismissingReminders = false;
    }

    // Entry Date is "dd/MM/yyyy" and Time is "HH:mm:ss".
    private static bool TryParseEntryTime(AttendanceEntry entry, out DateTime result) =>
        DateTime.TryParseExact(
            $"{entry.Date} {entry.Time}", "dd/MM/yyyy HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out result);

    // Attendance entry Mode is "in" or "out".
    private static bool IsIn(string mode) => mode.Equals("in", StringComparison.OrdinalIgnoreCase);
    private static bool IsOut(string mode) => mode.Equals("out", StringComparison.OrdinalIgnoreCase);

    private static Color GetStatusColor(string mode)
    {
        if (IsIn(mode)) return Color.ForestGreen;
        if (IsOut(mode)) return Color.Firebrick;
        return Color.Gray; // unknown status
    }

    private static Icon CreateStatusIcon(string mode)
    {
        var color = GetStatusColor(mode);

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var pen = new Pen(Color.White, 2);
            g.FillEllipse(brush, 3, 3, 26, 26);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }

        var hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    // NotifyIcon.Text has a 127-char limit.
    private static string Truncate(string text) =>
        text.Length <= 127 ? text : text[..124] + "...";

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _timer.Stop();
        _reminderTimer.Stop();
        DismissAllReminders();
        base.ExitThreadCore();
    }
}
