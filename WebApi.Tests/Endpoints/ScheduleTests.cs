using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class ScheduleTests : ApiTestBase
{
    private static readonly DateTime WindowStart = new(2034, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task Schedule_ReturnsClassesWithSeatsAndOwnParticipation()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var room = await TestData.CreateRoomAsync();
        var scheduledClass = await TestData.CreateClassAsync(
            capacity: 10,
            startsAt: WindowStart.AddHours(9),
            roomId: room.Id,
            durationMinutes: 90,
            title: "Boxing");
        await user.EnrollAsync(scheduledClass.Id, 2);
        await user.EnrollAsync(scheduledClass.Id, 1);
        await TestData.FillClassAsync(scheduledClass.Id, 4);
        var other = await TestData.CreateUserAsync();
        await TestData.CreateEnrollmentAsync(other.Id, scheduledClass.Id, 2, EnrollmentStatus.Cancelled);

        var json = await GetScheduleAsync(user, $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={room.Id}");

        ResponseAssert.HasExactlyProperties(json, "items", "continuationToken");
        var item = json.GetProperty("items").EnumerateArray().Single();
        ResponseAssert.HasExactlyProperties(
            item,
            "classId", "title", "startsAt", "durationMinutes", "room", "capacity", "freeSeats", "mySeats");
        ResponseAssert.HasExactlyProperties(item.GetProperty("room"), "id", "name");
        Assert.AreEqual(scheduledClass.Id, item.GetProperty("classId").GetInt64());
        Assert.AreEqual("Boxing", item.GetProperty("title").GetString());
        Assert.AreEqual(WindowStart.AddHours(9), item.GetProperty("startsAt").GetDateTime());
        Assert.AreEqual(90, item.GetProperty("durationMinutes").GetInt32());
        Assert.AreEqual(room.Id, item.GetProperty("room").GetProperty("id").GetInt64());
        Assert.AreEqual(room.Name, item.GetProperty("room").GetProperty("name").GetString());
        Assert.AreEqual(10, item.GetProperty("capacity").GetInt32());
        Assert.AreEqual(3, item.GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(3, item.GetProperty("mySeats").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("continuationToken").ValueKind);
    }

    [TestMethod]
    public async Task Schedule_MySeatsIsZeroWithoutOwnActiveEnrollments()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(1), roomId: room.Id);
        await TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 2, EnrollmentStatus.Cancelled);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        var item = (await GetItemsAsync(user, room.Id)).Single();

        Assert.AreEqual(0, item.GetProperty("mySeats").GetInt32());
    }

    [TestMethod]
    public async Task Schedule_FreeSeatsNeverNegative()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 5, startsAt: WindowStart.AddHours(1), roomId: room.Id);
        await TestData.FillClassAsync(scheduledClass.Id, 5);
        await TestData.SetClassAsync(scheduledClass.Id, capacity: 3);

        var item = (await GetItemsAsync(user, room.Id)).Single();

        Assert.AreEqual(0, item.GetProperty("freeSeats").GetInt32());
    }

    [TestMethod]
    public async Task Schedule_ExcludesClassesCancelledByClub()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var kept = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(1), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(2), roomId: room.Id, cancelled: true);

        var items = await GetItemsAsync(user, room.Id);

        CollectionAssert.AreEqual(new[] { kept.Id }, items.Select(x => x.GetProperty("classId").GetInt64()).ToArray());
    }

    [TestMethod]
    public async Task Schedule_FromIsInclusiveAndToIsExclusive()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var atFrom = await TestData.CreateClassAsync(startsAt: WindowStart, roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: WindowStart.AddDays(1), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: WindowStart.AddTicks(-10), roomId: room.Id);

        var items = await GetItemsAsync(user, room.Id);

        CollectionAssert.AreEqual(new[] { atFrom.Id }, items.Select(x => x.GetProperty("classId").GetInt64()).ToArray());
    }

    [TestMethod]
    public async Task Schedule_IsOrderedByStartTime()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var late = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(20), roomId: room.Id);
        var early = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(1), roomId: room.Id);
        var middle = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(10), roomId: room.Id);

        var items = await GetItemsAsync(user, room.Id);

        CollectionAssert.AreEqual(
            new[] { early.Id, middle.Id, late.Id },
            items.Select(x => x.GetProperty("classId").GetInt64()).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(7)]
    [DataRow(100)]
    public async Task Schedule_PagingReturnsEveryClassOnceInOrder(int limit)
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var expected = new List<long>();

        for (var i = 0; i < 7; i++)
        {
            var startsAt = WindowStart.AddHours(i / 2 * 3);
            expected.Add((await TestData.CreateClassAsync(startsAt: startsAt, roomId: room.Id)).Id);
        }

        var collected = new List<long>();
        string? token = null;
        var pages = 0;

        do
        {
            var query = $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={room.Id}&limit={limit}";

            if (token != null)
            {
                query += $"&continuationToken={Uri.EscapeDataString(token)}";
            }

            var json = await GetScheduleAsync(user, query);
            var items = json.GetProperty("items").EnumerateArray().ToList();

            Assert.IsTrue(items.Count <= limit);
            collected.AddRange(items.Select(x => x.GetProperty("classId").GetInt64()));
            token = json.GetProperty("continuationToken").GetString();
            pages++;
        }
        while (token != null);

        CollectionAssert.AreEqual(expected, collected);
        Assert.AreEqual((int)Math.Ceiling(7.0 / limit), pages);
    }

    [TestMethod]
    public async Task Schedule_PagingIsStableWhenClassesAreAddedBeforeCursor()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var first = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(1), roomId: room.Id);
        var second = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(2), roomId: room.Id);
        var third = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(3), roomId: room.Id);
        var baseQuery = $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={room.Id}&limit=2";

        var firstPage = await GetScheduleAsync(user, baseQuery);
        await TestData.CreateClassAsync(startsAt: WindowStart.AddMinutes(30), roomId: room.Id);
        var token = firstPage.GetProperty("continuationToken").GetString()!;
        var secondPage = await GetScheduleAsync(user, $"{baseQuery}&continuationToken={Uri.EscapeDataString(token)}");

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            firstPage.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()).ToArray());
        CollectionAssert.AreEqual(
            new[] { third.Id },
            secondPage.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()).ToArray());
    }

    [TestMethod]
    public async Task Schedule_DefaultLimitIsTwenty()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();

        for (var i = 0; i < 21; i++)
        {
            await TestData.CreateClassAsync(startsAt: WindowStart.AddMinutes(i), roomId: room.Id);
        }

        var json = await GetScheduleAsync(user, $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={room.Id}");

        Assert.AreEqual(20, json.GetProperty("items").GetArrayLength());
        Assert.IsNotNull(json.GetProperty("continuationToken").GetString());
    }

    [TestMethod]
    public async Task Schedule_UnknownRoomGivesEmptyList()
    {
        var user = await TestData.CreateUserAsync();

        var json = await GetScheduleAsync(user, "from=2026-01-01T00:00:00Z&to=2027-01-01T00:00:00Z&roomId=999999999");

        Assert.AreEqual(0, json.GetProperty("items").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("continuationToken").ValueKind);
    }

    [TestMethod]
    public async Task Schedule_WithoutRoomFilterIncludesAllRooms()
    {
        var user = await TestData.CreateUserAsync();
        var first = await TestData.CreateClassAsync(startsAt: new DateTime(2036, 7, 7, 10, 0, 0, DateTimeKind.Utc));
        var second = await TestData.CreateClassAsync(startsAt: new DateTime(2036, 7, 7, 11, 0, 0, DateTimeKind.Utc));

        var json = await GetScheduleAsync(user, "from=2036-07-07T00:00:00Z&to=2036-07-08T00:00:00Z");

        var ids = json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()).ToList();
        CollectionAssert.IsSubsetOf(new[] { first.Id, second.Id }, ids);
    }

    [TestMethod]
    [DataRow("from=2034-03-01T03:00:00%2B03:00&to=2034-03-01T05:00:00%2B03:00", DisplayName = "encoded positive offset")]
    [DataRow("from=2034-03-01T03:00:00+03:00&to=2034-03-01T05:00:00+03:00", DisplayName = "unencoded plus in offset")]
    [DataRow("from=2034-02-28T19:00:00-05:00&to=2034-02-28T21:00:00-05:00", DisplayName = "negative offset")]
    [DataRow("from=2034-03-01T00:00:00&to=2034-03-01T02:00:00", DisplayName = "no offset means UTC")]
    [DataRow("from=2034-03-01&to=2034-03-01T02:00:00Z", DisplayName = "date only is midnight UTC")]
    [DataRow("from=2034-03-01T00:00:00.0000000Z&to=2034-03-01T02:00Z", DisplayName = "fractions and minutes precision")]
    public async Task Schedule_AcceptsIso8601WithAndWithoutOffset(string query)
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var inside = await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(1), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(2), roomId: room.Id);

        var json = await GetScheduleAsync(user, $"{query}&roomId={room.Id}");

        CollectionAssert.AreEqual(
            new[] { inside.Id },
            json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()).ToArray());
    }

    [TestMethod]
    [DataRow("to=2034-03-02T00:00:00Z", DisplayName = "from missing")]
    [DataRow("from=2034-03-01T00:00:00Z", DisplayName = "to missing")]
    [DataRow("", DisplayName = "both missing")]
    [DataRow("from=&to=2034-03-02T00:00:00Z", DisplayName = "from empty")]
    [DataRow("from=yesterday&to=2034-03-02T00:00:00Z", DisplayName = "from not a date")]
    [DataRow("from=01.03.2034&to=02.03.2034", DisplayName = "non ISO format")]
    [DataRow("from=2034-13-01T00:00:00Z&to=2034-03-02T00:00:00Z", DisplayName = "month thirteen")]
    [DataRow("from=2034-03-01T25:00:00Z&to=2034-03-02T00:00:00Z", DisplayName = "hour twenty five")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-01T00:00:00Z", DisplayName = "to equals from")]
    [DataRow("from=2034-03-02T00:00:00Z&to=2034-03-01T00:00:00Z", DisplayName = "to before from")]
    [DataRow("from=2034-03-01T03:00:00+03:00&to=2034-03-01T00:00:00Z", DisplayName = "to equals from after offset")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId=abc", DisplayName = "roomId not a number")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId=1.5", DisplayName = "roomId fractional")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId=9223372036854775808", DisplayName = "roomId beyond long")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&limit=0", DisplayName = "limit zero")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&limit=101", DisplayName = "limit 101")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&limit=-1", DisplayName = "limit negative")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&limit=ten", DisplayName = "limit not a number")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&limit=", DisplayName = "limit empty")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&continuationToken=garbage", DisplayName = "token garbage")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&continuationToken=", DisplayName = "token empty")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&continuationToken=eyJhIjoxfQ", DisplayName = "token valid base64 wrong content")]
    public async Task Schedule_RejectsInvalidQuery(string query)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.GetAsync($"/schedule?{query}"), HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public async Task Schedule_AcceptsLimitBounds(int limit)
    {
        var user = await TestData.CreateUserAsync();

        var json = await GetScheduleAsync(user, $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId=0&limit={limit}");

        Assert.AreEqual(0, json.GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    [DataRow("from=2034-03-01T00:00:01Z&to=2034-03-02T00:00:00Z&roomId={0}", DisplayName = "different from")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-03T00:00:00Z&roomId={0}", DisplayName = "different to")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z", DisplayName = "room filter dropped")]
    [DataRow("from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId=1", DisplayName = "different room")]
    public async Task Schedule_TokenIsBoundToQueryParameters(string otherQueryTemplate)
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(1), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(2), roomId: room.Id);
        var firstPage = await GetScheduleAsync(
            user,
            $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={room.Id}&limit=1");
        var token = Uri.EscapeDataString(firstPage.GetProperty("continuationToken").GetString()!);
        var otherQuery = string.Format(otherQueryTemplate, room.Id);

        var response = await user.Client.GetAsync($"/schedule?{otherQuery}&continuationToken={token}");

        await ResponseAssert.ProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Schedule_TokenSurvivesDifferentLimit()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();

        for (var i = 0; i < 4; i++)
        {
            await TestData.CreateClassAsync(startsAt: WindowStart.AddHours(i), roomId: room.Id);
        }

        var baseQuery = $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={room.Id}";
        var firstPage = await GetScheduleAsync(user, $"{baseQuery}&limit=1");
        var token = Uri.EscapeDataString(firstPage.GetProperty("continuationToken").GetString()!);

        var rest = await GetScheduleAsync(user, $"{baseQuery}&limit=10&continuationToken={token}");

        Assert.AreEqual(3, rest.GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    public async Task Schedule_ShowsPastClassesInsidePeriod()
    {
        var user = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var past = await TestData.CreateClassAsync(startsIn: TimeSpan.FromDays(-3), roomId: room.Id);
        var from = Now.AddDays(-4).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var to = Now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ");

        var json = await GetScheduleAsync(user, $"from={from}&to={to}&roomId={room.Id}");

        Assert.AreEqual(past.Id, json.GetProperty("items")[0].GetProperty("classId").GetInt64());
    }

    [TestMethod]
    public async Task Schedule_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().GetAsync("/schedule?from=2034-03-01&to=2034-03-02");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<JsonElement> GetScheduleAsync(TestUser user, string query)
    {
        return await ResponseAssert.StatusAsync(await user.Client.GetAsync($"/schedule?{query}"), HttpStatusCode.OK);
    }

    private static async Task<List<JsonElement>> GetItemsAsync(TestUser user, long roomId)
    {
        var json = await GetScheduleAsync(
            user,
            $"from=2034-03-01T00:00:00Z&to=2034-03-02T00:00:00Z&roomId={roomId}&limit=100");

        return json.GetProperty("items").EnumerateArray().ToList();
    }
}
