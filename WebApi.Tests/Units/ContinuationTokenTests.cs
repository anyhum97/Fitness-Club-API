using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using WebApi.Services;

namespace WebApi.Tests.Units;

[TestClass]
public class ContinuationTokenTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(null)]
    [DataRow(42L)]
    public void EncodeDecode_RoundTrips(long? roomId)
    {
        var token = new ContinuationToken(new DateTime(2026, 1, 5, 10, 30, 0, 123, 456, DateTimeKind.Utc), 77, From, To, roomId);

        Assert.IsTrue(ContinuationToken.TryDecode(token.Encode(), out var decoded));
        Assert.AreEqual(token, decoded);
        Assert.AreEqual(DateTimeKind.Utc, decoded!.StartsAt.Kind);
        Assert.IsTrue(decoded.MatchesQuery(From, To, roomId));
    }

    [TestMethod]
    public void Encode_IsUrlSafe()
    {
        var encoded = new ContinuationToken(From, long.MaxValue, From, To, long.MinValue).Encode();

        Assert.IsFalse(encoded.Contains('+') || encoded.Contains('/') || encoded.Contains('='));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("%%%")]
    [DataRow("bm90LWpzb24")]
    [DataRow("bnVsbA")]
    public void TryDecode_RejectsGarbage(string value)
    {
        Assert.IsFalse(ContinuationToken.TryDecode(value, out var decoded));
        Assert.IsNull(decoded);
    }

    [TestMethod]
    public void TryDecode_RejectsTokenWithoutUtcMoment()
    {
        var json = """{"startsAt":"2026-01-05T10:30:00","classId":1,"from":"2026-01-01T00:00:00Z","to":"2026-02-01T00:00:00Z"}""";

        Assert.IsFalse(ContinuationToken.TryDecode(WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json)), out _));
    }

    [TestMethod]
    public void MatchesQuery_DetectsAnyDifference()
    {
        var token = new ContinuationToken(From, 1, From, To, 5);

        Assert.IsFalse(token.MatchesQuery(From.AddTicks(10), To, 5));
        Assert.IsFalse(token.MatchesQuery(From, To.AddDays(1), 5));
        Assert.IsFalse(token.MatchesQuery(From, To, null));
        Assert.IsFalse(token.MatchesQuery(From, To, 6));
    }
}
