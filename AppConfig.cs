using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkClock;

/// <summary>
/// Runtime settings loaded from an external <c>appsettings.json</c> file so they
/// can be edited without recompiling. Any key missing from the file falls back
/// to the default value defined here.
/// </summary>
public class AppConfig
{
    public string EmployeeId { get; init; } = "";
    public string BaseUrl { get; init; } = "https://your-attendance-portal.example.com";
    public string SessionFile { get; init; } = "session.json";
    public int RefreshIntervalMinutes { get; init; } = 5;
    public int LoginTimeoutMinutes { get; init; } = 3;
    public int BalloonDurationSeconds { get; init; } = 5;

    // "You've been in for a while" reminders. RemindersEnabled is mutable
    // because the tray menu can toggle it at runtime (and persist it).
    public bool RemindersEnabled { get; set; } = true;
    public int ReminderIntervalHours { get; init; } = 2;

    // After the first reminder, keep nagging this often (minutes) until you
    // checkpoint with a new "in" punch. Lets you catch a reminder you missed
    // sooner than waiting another full interval.
    public int ReminderFollowupMinutes { get; init; } = 30;

    private const string FileName = "appsettings.json";

    // Where this config was loaded from, so Save() can write back to it.
    [JsonIgnore] public string? SourcePath { get; private set; }

    /// <summary>
    /// Loads settings from <c>appsettings.json</c>, looking first next to the
    /// executable and then in the current working directory. Missing file or
    /// missing keys → built-in defaults.
    /// </summary>
    public static AppConfig Load()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) continue;

            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg is not null)
                {
                    cfg.SourcePath = path;
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                // Malformed file - keep running on defaults rather than crash.
                Console.WriteLine($"Could not read {FileName} ({ex.Message}). Using defaults.");
            }

            break; // Found the file (even if unreadable); don't keep searching.
        }

        return new AppConfig { SourcePath = Path.Combine(AppContext.BaseDirectory, FileName) };
    }

    /// <summary>
    /// Writes the current settings back to disk (used when the tray menu
    /// toggles the reminder on/off). Failures are swallowed - persistence is
    /// a convenience, not critical.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = SourcePath ?? Path.Combine(AppContext.BaseDirectory, FileName);
            File.WriteAllText(path,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not save {FileName} ({ex.Message}).");
        }
    }
}
