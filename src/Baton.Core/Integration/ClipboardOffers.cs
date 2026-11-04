using System.Security.Cryptography;

namespace Baton.Core.Integration;

/// <summary>
/// The links offered from the clipboard, waiting for a click on their notification. The buttons carry only a random reference
/// that expires soon; the address stays here, in memory, and is never written anywhere.
/// </summary>
public sealed class ClipboardOffers(TimeSpan? lifetime = null)
{
    private const int MaxOffers = 20;

    private readonly TimeSpan _lifetime = lifetime ?? ActionToasts.ClipboardLifetime;
    private readonly Dictionary<string, (string Url, DateTime ExpiresUtc)> _offers = [];
    private readonly object _lock = new();

    /// <summary>Keeps <paramref name="url"/> and returns the reference to put in the buttons.</summary>
    public string Add(string url, DateTime nowUtc)
    {
        var reference = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');
        lock (_lock)
        {
            Purge(nowUtc);
            if (_offers.Count >= MaxOffers)
                _offers.Remove(_offers.MinBy(o => o.Value.ExpiresUtc).Key);
            _offers[reference] = (url, nowUtc + _lifetime);
        }
        return reference;
    }

    /// <summary>The address of an offer that is still valid; the offer is consumed either way.</summary>
    public bool TryTake(string reference, DateTime nowUtc, out string url)
    {
        lock (_lock)
        {
            Purge(nowUtc);
            if (_offers.Remove(reference, out var offer))
            {
                url = offer.Url;
                return true;
            }
        }
        url = "";
        return false;
    }

    private void Purge(DateTime nowUtc)
    {
        foreach (var old in _offers.Where(o => o.Value.ExpiresUtc <= nowUtc).Select(o => o.Key).ToList())
            _offers.Remove(old);
    }
}
