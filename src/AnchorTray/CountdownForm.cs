namespace Anchor;

/// <summary>
/// Topmost countdown window shown while a shutdown is pending. The only way
/// to stop the shutdown from here is the "Cancel shutdown" button.
/// </summary>
public sealed class CountdownForm : Form
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _secondsLabel;
    private readonly Font _secondsFont;
    private readonly Action _cancelAction;

    private DateTimeOffset _deadline;

    public CountdownForm(DateTimeOffset deadline, Action cancelAction)
    {
        _deadline = deadline;
        _cancelAction = cancelAction;

        Text = "Anchor — shutdown pending";
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var message = new Label
        {
            Text = "Wall power is out and the power station battery is at the configured floor."
                   + Environment.NewLine + "Windows will shut down when the countdown reaches zero.",
            AutoSize = true,
            MaximumSize = new Size(400, 0), // Wrap width, scaled with DPI.
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None, // Centered in its table cell.
            Margin = new Padding(0, 0, 0, 12),
        };

        _secondsFont = new Font(Font.FontFamily, 42f, FontStyle.Bold);
        _secondsLabel = new Label
        {
            Text = "--",
            Font = _secondsFont,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 12),
        };

        var cancelButton = new Button
        {
            Text = "Cancel shutdown",
            AutoSize = true,
            MinimumSize = new Size(130, 32),
        };
        cancelButton.Click += (_, _) =>
        {
            _cancelAction();
            Close();
        };

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        buttonRow.Controls.Add(cancelButton);

        var table = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
        };
        for (var i = 0; i < 3; i++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(message, 0, 0);
        table.Controls.Add(_secondsLabel, 0, 1);
        table.Controls.Add(buttonRow, 0, 2);

        Controls.Add(table);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => RefreshCountdown();
        _timer.Start();
        RefreshCountdown();
    }

    /// <summary>Update the deadline the countdown is displayed against.</summary>
    public void UpdateDeadline(DateTimeOffset deadline)
    {
        _deadline = deadline;
        RefreshCountdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
        if (disposing)
            _secondsFont.Dispose();
    }

    private void RefreshCountdown()
    {
        var remaining = _deadline - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        _secondsLabel.Text = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
    }
}
