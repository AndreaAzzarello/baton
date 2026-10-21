using System.Globalization;
using System.Windows;
using Baton.Core.Net;
using Baton.Core.Resources;

namespace Baton.App.Views;

/// <summary>Asked when a server (401, FTP) or the proxy (407) wants a login. The password never leaves the password box in clear.</summary>
public partial class LoginWindow : Window
{
    public LoginWindow(LoginRequest request)
    {
        InitializeComponent();
        var host = request.Target.IsDefaultPort ? request.Target.Host : $"{request.Target.Host}:{request.Target.Port}";
        Title = request.IsProxy ? Strings.LoginProxyTitle : Strings.LoginTitle;
        Heading.Text = string.Format(CultureInfo.CurrentCulture, request.IsProxy ? Strings.LoginProxyHeading : Strings.LoginHeading, host);
        Details.Text = string.Format(CultureInfo.CurrentCulture, Strings.LoginDetails, request.DisplayName, request.Scheme)
                       + (request.Realm is { Length: > 0 } realm ? " " + string.Format(CultureInfo.CurrentCulture, Strings.LoginRealm, realm) : "");
        // Basic sends the password readable by anyone on the way when the connection is not encrypted.
        // Then the user must say explicitly that they accept it: the button stays disabled until they do.
        if (request.SendsPasswordInClear)
        {
            Warning.Text = Strings.LoginUnencrypted;
            PlainBox.Visibility = Visibility.Visible;
            SignInButton.IsEnabled = false;
        }
        Loaded += (_, _) =>
        {
            Activate();
            UserBox.Focus();
        };
    }

    public string Username => UserBox.Text.Trim();
    public string Password => PasswordBox.Password;
    public bool Remember => RememberBox.IsChecked == true;

    private void OnPlainChanged(object sender, RoutedEventArgs e) => SignInButton.IsEnabled = PlainBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (PlainBox.Visibility == Visibility.Visible && PlainBox.IsChecked != true)
        {
            PlainBox.Focus();
            return;
        }
        if (Username.Length == 0)
        {
            UserBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
