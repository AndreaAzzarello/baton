using System.Globalization;
using System.Windows;
using DownloadManager.Core.Net;
using DownloadManager.Core.Resources;

namespace DownloadManager.App.Views;

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
        if (!request.IsProxy && request.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) && request.Target.Scheme == "http"
            || request.Scheme == "FTP" && request.Target.Scheme == "ftp")
            Warning.Text = Strings.LoginUnencrypted;
        Loaded += (_, _) =>
        {
            Activate();
            UserBox.Focus();
        };
    }

    public string Username => UserBox.Text.Trim();
    public string Password => PasswordBox.Password;
    public bool Remember => RememberBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Username.Length == 0)
        {
            UserBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
