namespace WebApi.Models;

public record RoomResponse(long Id, string Name);

public record EnrollmentResponse(long Id, long ClassId, int Seats, string Status, DateTime CreatedAt);

public record EnrollmentClassResponse(
    long Id,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    bool IsCancelled,
    RoomResponse Room);

public record PassResponse(int RemainingVisits, DateOnly ValidUntil);

public record EnrollmentCardResponse(
    long Id,
    string Status,
    int Seats,
    DateTime CreatedAt,
    EnrollmentClassResponse Class,
    PassResponse Pass);

public record CancelEnrollmentResponse(long Id, string Status, int RefundedVisits);

public record WaitlistEntryResponse(long Id, long ClassId, int Seats, int Position, DateTime CreatedAt);

public record CancelWaitlistEntryResponse(long Id, string Status);

public record ScheduleItemResponse(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    RoomResponse Room,
    int Capacity,
    int FreeSeats,
    int MySeats);

public record ScheduleResponse(IReadOnlyList<ScheduleItemResponse> Items, string? ContinuationToken);

public record RoomLoadItemResponse(
    DateOnly Date,
    RoomResponse Room,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);

public record RoomLoadResponse(IReadOnlyList<RoomLoadItemResponse> Items);
