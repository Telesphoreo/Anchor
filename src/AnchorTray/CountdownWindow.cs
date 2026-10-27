using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Anchor;

/// <summary>
/// Topmost countdown window shown while a shutdown is pending. The only way
/// to stop the shutdown from here is the "Cancel shutdown" button; closing
/// the window only hides the countdown.
/// </summary>
public sealed class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _secondsText;
    private readonly Action _cancelAction;

    private DateTimeOffset _deadline;

    public CountdownWindow(DateTimeOffset deadline, Action cancelAction)
    {
        _deadline = deadline;
        _cancelAction = cancelAction;

        Title = "Anchor: shutdown pending";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var message = new TextBlock
        {
            Text = "Wall power is out and the power station battery is at the configured floor."
                   + Environment.NewLine + "The computer will shut down when the countdown reaches zero.",
            MaxWidth = 400,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };

        _secondsText = new TextBlock
        {
            Text = "--",
            FontSize = 56,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var cancelButton = new Button
        {
            Content = "Cancel shutdown",
            MinWidth = 130,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancelButton.Click += (_, _) =>
        {
            _cancelAction();
            Close();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { message, _secondsText, cancelButton },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshCountdown();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        RefreshCountdown();
    }

    /// <summary>Update the deadline the countdown is displayed against.</summary>
    public void UpdateDeadline(DateTimeOffset deadline)
    {
        _deadline = deadline;
        RefreshCountdown();
    }

    private void RefreshCountdown()
    {
        var remaining = _deadline - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        _secondsText.Text = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
    }
}
