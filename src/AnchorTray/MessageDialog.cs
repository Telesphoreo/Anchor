using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Anchor;

/// <summary>A modal message with an OK button.</summary>
public sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 88,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        okButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, MaxWidth = 400, TextWrapping = TextWrapping.Wrap },
                okButton,
            },
        };
    }

    public static Task ShowAsync(Window owner, string title, string message) =>
        new MessageDialog(title, message).ShowDialog(owner);
}
