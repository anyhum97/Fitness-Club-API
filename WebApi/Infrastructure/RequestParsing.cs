using System.Globalization;
using System.Text.RegularExpressions;

namespace WebApi.Infrastructure;

public static partial class RequestParsing
{
    public static bool TryParseMoment(string? value, out DateTime utc)
    {
        utc = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // решение: «+» в смещении, переданный в строке запроса без кодирования, приходит пробелом;
        // восстанавливаем его, чтобы 2026-09-22T10:00:00+03:00 не отклонялся.
        var normalized = DecodedPlusInOffset().Replace(value.Trim(), "$1+$2");

        if (!IsoMoment().IsMatch(normalized))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;

        return true;
    }

    public static bool TryParseDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    public static bool TryParseLong(string? value, out long result)
    {
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    public static bool TryParseInt(string? value, out int result)
    {
        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?([Zz]|[+-]\d{2}:\d{2})?)?$")]
    private static partial Regex IsoMoment();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T[\d:.]+) (\d{2}:\d{2})$")]
    private static partial Regex DecodedPlusInOffset();
}
