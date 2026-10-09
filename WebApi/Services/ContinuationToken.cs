using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace WebApi.Services;

public record ContinuationToken(DateTime StartsAt, long ClassId, DateTime From, DateTime To, long? RoomId)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string Encode()
    {
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, SerializerOptions)));
    }

    public static bool TryDecode(string value, out ContinuationToken? token)
    {
        token = null;

        try
        {
            token = JsonSerializer.Deserialize<ContinuationToken>(
                Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value)),
                SerializerOptions);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }

        if (token == null || token.StartsAt.Kind != DateTimeKind.Utc)
        {
            token = null;

            return false;
        }

        return true;
    }

    public bool MatchesQuery(DateTime from, DateTime to, long? roomId)
    {
        return From == from && To == to && RoomId == roomId;
    }
}
