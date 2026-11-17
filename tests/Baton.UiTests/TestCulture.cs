using System.Globalization;

namespace Baton.UiTests;

/// <summary>
/// A test that reads or writes numbers, dates or texts the way a culture does must say which culture it means, and put the thread back
/// as it found it: <c>using var _ = TestCulture.Use("it-IT");</c>. Without it the result depends on the machine (the CI runs in
/// English) and on which test ran before (other tests change the language of the process).
/// </summary>
internal static class TestCulture
{
    public static IDisposable Use(string name) => new Scope(CultureInfo.GetCultureInfo(name));

    private sealed class Scope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public Scope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
