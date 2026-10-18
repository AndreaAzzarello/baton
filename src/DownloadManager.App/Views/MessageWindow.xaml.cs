using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DownloadManager.App.Services;

namespace DownloadManager.App.Views;

public enum MessageKind
{
    Question,
    Information,
    Error,
}

/// <summary>A message box that follows the app's theme; buttons are plain buttons, reachable by keyboard and screen readers.</summary>
public partial class MessageWindow : Window
{
    private Answer _answer = Answer.Cancel;

    /// <param name="details">Technical details (an unexpected error): shown folded, with a "Copy details" button.</param>
    public MessageWindow(string message, string title, MessageKind kind, IReadOnlyList<(string Text, Answer Answer)> buttons, Answer defaultAnswer,
        string? details = null)
    {
        InitializeComponent();
        if (details is not null)
        {
            DetailsText.Text = details;
            DetailsPanel.Visibility = Visibility.Visible;
            CopyDetails.Visibility = Visibility.Visible;
            Width = 560;
        }
        Title = title;
        MessageText.Text = message;
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetHelpText(this, message);
        IconGlyph.Text = kind switch
        {
            MessageKind.Error => "",
            MessageKind.Information => "",
            _ => "",
        };
        if (kind == MessageKind.Error)
            IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

        foreach (var (text, answer) in buttons)
        {
            var isDefault = answer == defaultAnswer;
            var button = new Button
            {
                Content = text,
                IsDefault = isDefault,
                IsCancel = answer == Answer.Cancel || buttons.Count == 1,
            };
            button.SetResourceReference(StyleProperty, isDefault ? "PrimaryButton" : "DialogButton");
            button.Click += (_, _) =>
            {
                _answer = answer;
                DialogResult = true;
            };
            Buttons.Children.Add(button);
            if (isDefault)
                Loaded += (_, _) => button.Focus();
        }
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailsText.Text);
            CopiedText.Text = Core.Resources.Strings.ErrorDetailsCopied;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy (another program holds it): the details stay visible to copy by hand.
            DetailsPanel.IsExpanded = true;
        }
    }

    /// <summary>The button chosen; closing the window counts as Cancel.</summary>
    public Answer Answer => _answer;
}
