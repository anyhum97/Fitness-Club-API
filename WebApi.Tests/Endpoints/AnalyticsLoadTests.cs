using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class AnalyticsLoadTests : ApiTestBase
{
    [TestMethod]
    public async Task Load_AggregatesByUtcDateAndRoom()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var roomA = await TestData.CreateRoomAsync();
        var roomB = await TestData.CreateRoomAsync();
        var day1 = new DateTime(2037, 4, 10, 0, 0, 0, DateTimeKind.Utc);
        var day2 = day1.AddDays(1);

        var a1 = await TestData.CreateClassAsync(capacity: 10, startsAt: day1.AddHours(8), roomId: roomA.Id);
        var a2 = await TestData.CreateClassAsync(capacity: 5, startsAt: day1.AddHours(23).AddMinutes(59), roomId: roomA.Id);
        var b1 = await TestData.CreateClassAsync(capacity: 7, startsAt: day1.AddHours(12), roomId: roomB.Id);
        var a3 = await TestData.CreateClassAsync(capacity: 8, startsAt: day2, roomId: roomA.Id);
        await TestData.CreateClassAsync(capacity: 50, startsAt: day1.AddHours(9), roomId: roomA.Id, cancelled: true);

        await TestData.FillClassAsync(a1.Id, 4);
        await TestData.FillClassAsync(a1.Id, 2);
        await TestData.FillClassAsync(a2.Id, 5);
        await TestData.FillClassAsync(b1.Id, 1);
        var other = await TestData.CreateUserAsync();
        await TestData.CreateEnrollmentAsync(other.Id, a3.Id, 3, EnrollmentStatus.Cancelled);

        var items = await GetItemsAsync(admin, "from=2037-04-10&to=2037-04-11");

        Assert.AreEqual(3, items.Count);
        AssertRow(items[0], "2037-04-10", roomA.Id, roomA.Name, classes: 2, capacity: 15, booked: 11);
        AssertRow(items[1], "2037-04-10", roomB.Id, roomB.Name, classes: 1, capacity: 7, booked: 1);
        AssertRow(items[2], "2037-04-11", roomA.Id, roomA.Name, classes: 1, capacity: 8, booked: 0);
    }

    [TestMethod]
    public async Task Load_ItemHasExactlyContractFields()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var room = await TestData.CreateRoomAsync();
        await TestData.CreateClassAsync(startsAt: new DateTime(2037, 5, 1, 10, 0, 0, DateTimeKind.Utc), roomId: room.Id);

        var json = await GetLoadAsync(admin, $"from=2037-05-01&to=2037-05-01&roomId={room.Id}");

        ResponseAssert.HasExactlyProperties(json, "items");
        var item = json.GetProperty("items")[0];
        ResponseAssert.HasExactlyProperties(item, "date", "room", "classesCount", "totalCapacity", "bookedSeats");
        ResponseAssert.HasExactlyProperties(item.GetProperty("room"), "id", "name");
    }

    [TestMethod]
    public async Task Load_FromEqualToToIsOneDay()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var room = await TestData.CreateRoomAsync();
        await TestData.CreateClassAsync(startsAt: new DateTime(2037, 6, 1, 0, 0, 0, DateTimeKind.Utc), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: new DateTime(2037, 6, 1, 23, 59, 59, DateTimeKind.Utc), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: new DateTime(2037, 6, 2, 0, 0, 0, DateTimeKind.Utc), roomId: room.Id);
        await TestData.CreateClassAsync(startsAt: new DateTime(2037, 5, 31, 23, 59, 59, DateTimeKind.Utc), roomId: room.Id);

        var items = await GetItemsAsync(admin, $"from=2037-06-01&to=2037-06-01&roomId={room.Id}");

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(2, items[0].GetProperty("classesCount").GetInt32());
    }

    [TestMethod]
    public async Task Load_FilteringByRoomReturnsOnlyThatRoom()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var wanted = await TestData.CreateRoomAsync();
        var other = await TestData.CreateRoomAsync();
        var day = new DateTime(2037, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        await TestData.CreateClassAsync(startsAt: day, roomId: wanted.Id);
        await TestData.CreateClassAsync(startsAt: day, roomId: other.Id);

        var items = await GetItemsAsync(admin, $"from=2037-07-01&to=2037-07-01&roomId={wanted.Id}");

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(wanted.Id, items[0].GetProperty("room").GetProperty("id").GetInt64());
    }

    [TestMethod]
    public async Task Load_DayWithOnlyCancelledClassesHasNoRow()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var room = await TestData.CreateRoomAsync();
        await TestData.CreateClassAsync(
            startsAt: new DateTime(2037, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            roomId: room.Id,
            cancelled: true);

        var items = await GetItemsAsync(admin, $"from=2037-08-01&to=2037-08-31&roomId={room.Id}");

        Assert.AreEqual(0, items.Count);
    }

    [TestMethod]
    public async Task Load_UnknownRoomGivesEmptyList()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");

        var items = await GetItemsAsync(admin, "from=2020-01-01&to=2030-01-01&roomId=999999999");

        Assert.AreEqual(0, items.Count);
    }

    [TestMethod]
    public async Task Load_CountsSeededDataWithoutRoomFilter()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var from = Clock.Today.AddDays(-60).ToString("yyyy-MM-dd");
        var to = Clock.Today.AddDays(60).ToString("yyyy-MM-dd");

        var items = await GetItemsAsync(admin, $"from={from}&to={to}");

        Assert.IsTrue(items.Count > 0);
        var keys = items.Select(x => (x.GetProperty("date").GetString(), x.GetProperty("room").GetProperty("id").GetInt64())).ToList();
        CollectionAssert.AreEqual(keys.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList(), keys);
        Assert.IsTrue(items.All(x => x.GetProperty("bookedSeats").GetInt32() <= x.GetProperty("totalCapacity").GetInt32()));
    }

    [TestMethod]
    public async Task Load_UserRoleIsForbidden()
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.GetAsync("/analytics/load?from=2037-01-01&to=2037-01-02");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Load_ForbiddenIsReportedBeforeBadRequest()
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.GetAsync("/analytics/load?from=nonsense");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Load_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().GetAsync("/analytics/load?from=2037-01-01&to=2037-01-02");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("to=2037-01-02", DisplayName = "from missing")]
    [DataRow("from=2037-01-01", DisplayName = "to missing")]
    [DataRow("", DisplayName = "both missing")]
    [DataRow("from=2037-01-01T00:00:00Z&to=2037-01-02", DisplayName = "from with time")]
    [DataRow("from=2037-01-01&to=2037-01-02T10:00:00", DisplayName = "to with time")]
    [DataRow("from=2037-01-01&to=2037-01-02T00:00:00%2B03:00", DisplayName = "to with offset")]
    [DataRow("from=01.01.2037&to=02.01.2037", DisplayName = "non ISO date")]
    [DataRow("from=2037-1-1&to=2037-1-2", DisplayName = "single digit month and day")]
    [DataRow("from=2037-02-30&to=2037-03-01", DisplayName = "impossible date")]
    [DataRow("from=2037-01-02&to=2037-01-01", DisplayName = "to before from")]
    [DataRow("from=2037-01-01&to=2037-01-02&roomId=abc", DisplayName = "roomId not a number")]
    [DataRow("from=2037-01-01&to=2037-01-02&roomId=", DisplayName = "roomId empty")]
    [DataRow("from=2037-01-01&to=2037-01-02&roomId=99999999999999999999", DisplayName = "roomId beyond long")]
    public async Task Load_RejectsInvalidQuery(string query)
    {
        var admin = await TestData.CreateUserAsync(role: "admin");

        await ResponseAssert.ProblemAsync(await admin.Client.GetAsync($"/analytics/load?{query}"), HttpStatusCode.BadRequest);
    }

    private static async Task<JsonElement> GetLoadAsync(TestUser admin, string query)
    {
        return await ResponseAssert.StatusAsync(await admin.Client.GetAsync($"/analytics/load?{query}"), HttpStatusCode.OK);
    }

    private static async Task<List<JsonElement>> GetItemsAsync(TestUser admin, string query)
    {
        return (await GetLoadAsync(admin, query)).GetProperty("items").EnumerateArray().ToList();
    }

    private static void AssertRow(
        JsonElement row,
        string date,
        long roomId,
        string roomName,
        int classes,
        int capacity,
        int booked)
    {
        Assert.AreEqual(date, row.GetProperty("date").GetString());
        Assert.AreEqual(roomId, row.GetProperty("room").GetProperty("id").GetInt64());
        Assert.AreEqual(roomName, row.GetProperty("room").GetProperty("name").GetString());
        Assert.AreEqual(classes, row.GetProperty("classesCount").GetInt32());
        Assert.AreEqual(capacity, row.GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(booked, row.GetProperty("bookedSeats").GetInt32());
    }
}
