using System.Globalization;

namespace Baton.App;

internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        var format = unit == 0 || bytes >= 100 ? "0" : "0.0";
        return bytes.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : Bytes(bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? value)
    {
        if (value is not { } t || t < TimeSpan.Zero || t > TimeSpan.FromDays(30))
            return "";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} h {t.Minutes:00} min";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} min {t.Seconds:00} s";
        return $"{Math.Max(1, t.Seconds)} s";
    }
}
