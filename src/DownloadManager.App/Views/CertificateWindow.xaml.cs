using System.Globalization;
using System.Net.Security;
using System.Windows;
using DownloadManager.App.ViewModels;
using DownloadManager.Core.Resources;
using DownloadManager.Core.Security;

namespace DownloadManager.App.Views;

/// <summary>
/// A server certificate Windows does not trust. Refusing is the default (Enter, Esc and closing all refuse):
/// accepting always takes a deliberate click.
/// </summary>
public partial class CertificateWindow : Window
{
    public CertificateWindow(CertificateQuestion question)
    {
        InitializeComponent();
        Heading.Text = string.Format(CultureInfo.CurrentCulture, Strings.CertificateHeading, $"{question.Host}:{question.Port}");
        var problems = new List<string>();
        if (question.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            problems.Add(Strings.CertificateNameMismatch);
        if (question.Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            problems.Add(question.NotAfter < DateTime.UtcNow || question.NotBefore > DateTime.UtcNow ? Strings.CertificateExpired : Strings.CertificateUntrustedIssuer);
        if (question.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            problems.Add(Strings.CertificateMissing);
        Problems.Text = string.Join(" ", problems);
        Subject.Text = question.Subject;
        Issuer.Text = question.Issuer;
        Validity.Text = string.Format(CultureInfo.CurrentCulture, Strings.CertificateValidityRange, question.NotBefore.ToLocalTime(), question.NotAfter.ToLocalTime());
        Fingerprint.Text = CertificateText.Fingerprint(question.Fingerprint);
        Loaded += (_, _) =>
        {
            Activate();
            RejectButton.Focus();
        };
    }

    public CertificateDecision Decision { get; private set; } = CertificateDecision.Reject;

    private void OnAlways(object sender, RoutedEventArgs e) => Finish(CertificateDecision.AcceptAlways);

    private void OnOnce(object sender, RoutedEventArgs e) => Finish(CertificateDecision.AcceptOnce);

    private void OnReject(object sender, RoutedEventArgs e) => Finish(CertificateDecision.Reject);

    private void Finish(CertificateDecision decision)
    {
        Decision = decision;
        DialogResult = decision != CertificateDecision.Reject;
    }
}
