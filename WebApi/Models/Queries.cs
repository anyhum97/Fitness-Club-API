using System.ComponentModel.DataAnnotations;

namespace WebApi.Models;

public class ScheduleQuery
{
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? From { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? To { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? RoomId { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Limit { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? ContinuationToken { get; set; }
}

public class RoomLoadQuery
{
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? From { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? To { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? RoomId { get; set; }
}
