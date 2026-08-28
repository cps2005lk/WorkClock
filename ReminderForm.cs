using System.Drawing;
using System.Windows.Forms;

namespace WorkClock;

/// <summary>
/// A small always-on-top window that stays visible until the user clicks
/// Dismiss. Used for the "you've been in for N hours" reminders.
/// </summary>
public class ReminderForm : Form
{
    private readonly Rectangle _screenBounds;

    /// <param name="screenBounds">
    /// The working area of the monitor to center this window on.
    /// </param>
    public ReminderForm(string message, Rectangle screenBounds)
    {
        _screenBounds = screenBounds;

        Text = "WorkClock - Reminder";
        StartPosition = FormStartPosition.Manual; // we position it per-monitor
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(380, 150);

        var dismiss = new Button
        {
            Text = "Dismiss",
            Dock = DockStyle.Bottom,
            Height = 44
        };
        dismiss.Click += (_, _) => Close();

        var label = new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(16),
            Font = new Font("Segoe UI", 10F)
        };

        Controls.Add(label);   // fills remaining space
        Controls.Add(dismiss); // pinned to the bottom
        AcceptButton = dismiss;
    }

    // Center on the target monitor once the final size is known.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Location = new Point(
            _screenBounds.X + (_screenBounds.Width - Width) / 2,
            _screenBounds.Y + (_screenBounds.Height - Height) / 2);
    }

    // Make sure the window pops to the foreground when shown.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        BringToFront();
    }
}
