using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.Views;

/// <summary>
/// The warning before a queue's end action: counts down, and the action happens only if nobody cancels.
/// "Cancel" is the default button, so Enter, Esc and closing the window all cancel.
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _actionText;
    private int _remaining;
    private bool _confirmed;

    public CountdownWindow(EndAction action, string queueName, TimeSpan length)
    {
        InitializeComponent();
        _remaining = (int)length.TotalSeconds;
        _actionText = ActionText(action);
        Title = string.Format(CultureInfo.CurrentCulture, Strings.CountdownTitle, _actionText);
        Heading.Text = string.Format(CultureInfo.CurrentCulture, Strings.CountdownHeading, queueName);
        NowButton.Content = string.Format(CultureInfo.CurrentCulture, Strings.CountdownNow, _actionText);
        Progress.Maximum = _remaining;
        Show(_remaining);
        _timer.Tick += (_, _) =>
        {
            Show(--_remaining);
            if (_remaining <= 0)
                Finish(true);
        };
        Loaded += (_, _) =>
        {
            Activate();
            CancelButton.Focus();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>True when the countdown reached zero or the user chose to go ahead now.</summary>
    public bool Confirmed => _confirmed;

    public static string ActionText(EndAction action) => action switch
    {
        EndAction.CloseApp => Strings.EndActionCloseApp,
        EndAction.Sleep => Strings.EndActionSleep,
        EndAction.Hibernate => Strings.EndActionHibernate,
        EndAction.Shutdown => Strings.EndActionShutdown,
        _ => Strings.EndActionNone,
    };

    private void Show(int seconds)
    {
        Seconds.Text = seconds.ToString(CultureInfo.CurrentCulture);
        Progress.Value = seconds;
        Message.Text = string.Format(CultureInfo.CurrentCulture, Strings.CountdownMessage, LowerFirst(_actionText), seconds);
        if (seconds % 10 == 0 || seconds <= 5)
        {
            Announcement.Text = string.Format(CultureInfo.CurrentCulture, Strings.CountdownAnnouncement, seconds);
            UIElementAutomationPeer.FromElement(Announcement)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>"Spegni il PC" inside a sentence: only the first letter changes ("spegni il PC").</summary>
    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLower(text[0], CultureInfo.CurrentCulture) + text[1..];

    private void OnNow(object sender, RoutedEventArgs e) => Finish(true);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool confirmed)
    {
        _timer.Stop();
        _confirmed = confirmed;
        DialogResult = confirmed;
    }
}
