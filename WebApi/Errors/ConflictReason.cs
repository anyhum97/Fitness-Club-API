namespace WebApi.Errors;

public static class ConflictReason
{
    public const string CapacityExceeded = "capacity-exceeded";

    public const string PassExhausted = "pass-exhausted";

    public const string PassExpired = "pass-expired";

    public const string ClassStarted = "class-started";

    public const string ClassCancelled = "class-cancelled";

    public const string EnrollmentCancelled = "enrollment-cancelled";

    public const string SeatsAvailable = "seats-available";

    public const string AlreadyWaiting = "already-waiting";

    public const string WaitlistFulfilled = "waitlist-fulfilled";

    public const string IdempotencyKeyConflict = "idempotency-key-conflict";
}
