namespace MeetingRoomBooking.Models;

public class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Status { get; set; } = "ACTIVE"; // ACTIVE or CANCELLED
    public DateTime CreatedAt { get; set; }
}
