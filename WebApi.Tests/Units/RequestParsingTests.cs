using WebApi.Infrastructure;

namespace WebApi.Tests.Units;

[TestClass]
public class RequestParsingTests
{
    [TestMethod]
    [DataRow("2026-09-22", "2026-09-22T00:00:00.0000000Z")]
    [DataRow("2026-09-22T10:15", "2026-09-22T10:15:00.0000000Z")]
    [DataRow("2026-09-22T10:15:30", "2026-09-22T10:15:30.0000000Z")]
    [DataRow("2026-09-22T10:15:30Z", "2026-09-22T10:15:30.0000000Z")]
    [DataRow("2026-09-22T10:15:30z", "2026-09-22T10:15:30.0000000Z")]
    [DataRow("2026-09-22T10:15:30.123456Z", "2026-09-22T10:15:30.1234560Z")]
    [DataRow("2026-09-22T13:15:30+03:00", "2026-09-22T10:15:30.0000000Z")]
    [DataRow("2026-09-22T13:15:30 03:00", "2026-09-22T10:15:30.0000000Z")]
    [DataRow("2026-09-22T05:15:30-05:00", "2026-09-22T10:15:30.0000000Z")]
    [DataRow("2026-09-23T01:00:00+14:00", "2026-09-22T11:00:00.0000000Z")]
    public void TryParseMoment_AcceptsIsoAndConvertsToUtc(string value, string expected)
    {
        Assert.IsTrue(RequestParsing.TryParseMoment(value, out var parsed));
        Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
        Assert.AreEqual(expected, parsed.ToString("O"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("tomorrow")]
    [DataRow("22.09.2026")]
    [DataRow("09/22/2026")]
    [DataRow("2026-9-22")]
    [DataRow("2026-02-30")]
    [DataRow("2026-09-22T24:00:00Z")]
    [DataRow("2026-09-22 10:15:30")]
    [DataRow("2026-09-22T10:15:30+0300")]
    [DataRow("1726999200")]
    public void TryParseMoment_RejectsOtherFormats(string? value)
    {
        Assert.IsFalse(RequestParsing.TryParseMoment(value, out _));
    }

    [TestMethod]
    [DataRow("2026-09-22", 2026, 9, 22)]
    [DataRow("0001-01-01", 1, 1, 1)]
    [DataRow("9999-12-31", 9999, 12, 31)]
    [DataRow("2028-02-29", 2028, 2, 29)]
    public void TryParseDate_AcceptsPlainDates(string value, int year, int month, int day)
    {
        Assert.IsTrue(RequestParsing.TryParseDate(value, out var parsed));
        Assert.AreEqual(new DateOnly(year, month, day), parsed);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("2026-09-22T00:00:00")]
    [DataRow("2026-09-22T00:00:00Z")]
    [DataRow("2026-09-22Z")]
    [DataRow("2026-9-22")]
    [DataRow("2027-02-29")]
    [DataRow(" 2026-09-22")]
    public void TryParseDate_RejectsAnythingElse(string? value)
    {
        Assert.IsFalse(RequestParsing.TryParseDate(value, out _));
    }

    [TestMethod]
    [DataRow("0", 0L)]
    [DataRow("-15", -15L)]
    [DataRow("9223372036854775807", long.MaxValue)]
    public void TryParseLong_AcceptsIntegers(string value, long expected)
    {
        Assert.IsTrue(RequestParsing.TryParseLong(value, out var parsed));
        Assert.AreEqual(expected, parsed);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.0")]
    [DataRow("1e3")]
    [DataRow("0x10")]
    [DataRow(" 1")]
    [DataRow("9223372036854775808")]
    public void TryParseLong_RejectsNonIntegers(string value)
    {
        Assert.IsFalse(RequestParsing.TryParseLong(value, out _));
    }

    [TestMethod]
    [DataRow("2147483648")]
    [DataRow("1,5")]
    public void TryParseInt_RejectsOutOfRange(string value)
    {
        Assert.IsFalse(RequestParsing.TryParseInt(value, out _));
    }
}
