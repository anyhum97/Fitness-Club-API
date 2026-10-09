using System.ComponentModel.DataAnnotations;

namespace WebApi.Models;

public class CreateEnrollmentRequest
{
    [Required]
    public long? ClassId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}

public class ChangeSeatsRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}

public class JoinWaitlistRequest
{
    [Required]
    public long? ClassId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}
