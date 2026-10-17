using System.Globalization;
using MeetingRoomBooking.Data;
using MeetingRoomBooking.Models;

namespace MeetingRoomBooking.Services;

public record BookingResult(
    bool Success, 
    string Message, 
    int? BookingId = null, 
    int? ClashingBookingId = null
);

public class BookingService
{
    private readonly Database _db;
    public const string DateFormat = "yyyy-MM-dd HH:mm";

    public BookingService(Database db)
    {
        _db = db;
    }

    public BookingResult BookRoom(string roomName, string userId, string startStr, string endStr)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            return new BookingResult(false, "Error: room name cannot be empty");

        if (string.IsNullOrWhiteSpace(userId))
            return new BookingResult(false, "Error: user id cannot be empty");

        var room = _db.GetRoomByName(roomName.Trim());
        if (room == null)
            return new BookingResult(false, $"Error: room {roomName} does not exist");

        // 1. Parse dates
        if (!DateTime.TryParseExact(startStr, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return new BookingResult(false, $"Error: invalid start time format '{startStr}'. Expected YYYY-MM-DD HH:MM");

        if (!DateTime.TryParseExact(endStr, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return new BookingResult(false, $"Error: invalid end time format '{endStr}'. Expected YYYY-MM-DD HH:MM");

        // 2. Validate time rules
        if (start.Minute % 15 != 0 || start.Second != 0)
            return new BookingResult(false, "Error: start time must be in 15-minute steps (:00, :15, :30, :45)");

        if (end.Minute % 15 != 0 || end.Second != 0)
            return new BookingResult(false, "Error: end time must be in 15-minute steps (:00, :15, :30, :45)");

        if (end <= start)
            return new BookingResult(false, "Error: end time must be after start time");

        if (start.Date != end.Date)
            return new BookingResult(false, "Error: booking must start and end on the same single day");

        var duration = end - start;
        if (duration < TimeSpan.FromMinutes(15))
            return new BookingResult(false, "Error: booking must last at least 15 minutes");

        if (duration > TimeSpan.FromHours(4))
            return new BookingResult(false, "Error: booking cannot exceed 4 hours");

        string formattedStart = start.ToString(DateFormat);
        string formattedEnd = end.ToString(DateFormat);
        string datePrefix = start.ToString("yyyy-MM-dd");

        // 3. Attempt creation via Database atomic transaction
        var result = _db.TryCreateBooking(room.Id, userId.Trim(), formattedStart, formattedEnd, datePrefix);
        if (!result.Success)
        {
            if (result.ClashingBooking != null)
            {
                var clash = result.ClashingBooking;
                return new BookingResult(
                    false, 
                    $"Error: clashes with active booking {clash.Id} ({clash.Start:yyyy-MM-dd HH:mm} - {clash.End:yyyy-MM-dd HH:mm})",
                    ClashingBookingId: clash.Id
                );
            }

            return new BookingResult(false, $"Error: {result.ErrorMessage}");
        }

        return new BookingResult(
            true, 
            $"Booking {result.CreatedBooking!.Id} confirmed for room {room.Name} from {formattedStart} to {formattedEnd}", 
            BookingId: result.CreatedBooking.Id
        );
    }

    public BookingResult CancelBooking(string bookingIdStr)
    {
        if (!int.TryParse(bookingIdStr, out int bookingId) || bookingId <= 0)
            return new BookingResult(false, "Error: booking id must be a positive integer");

        var existing = _db.GetBookingById(bookingId);
        if (existing == null)
            return new BookingResult(false, $"Error: booking {bookingId} does not exist");

        if (existing.Status == "CANCELLED")
            return new BookingResult(false, $"Error: booking {bookingId} is already cancelled");

        bool success = _db.CancelBooking(bookingId);
        if (!success)
            return new BookingResult(false, $"Error: failed to cancel booking {bookingId}");

        return new BookingResult(true, $"Booking {bookingId} cancelled; slot is now free", BookingId: bookingId);
    }

    public (bool Success, string Message, List<Room> Rooms) GetFreeRooms(string startStr, string endStr, int minCapacity = 0)
    {
        if (!DateTime.TryParseExact(startStr, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return (false, $"Error: invalid start time format '{startStr}'. Expected YYYY-MM-DD HH:MM", new());

        if (!DateTime.TryParseExact(endStr, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return (false, $"Error: invalid end time format '{endStr}'. Expected YYYY-MM-DD HH:MM", new());

        if (end <= start)
            return (false, "Error: end time must be after start time", new());

        if (start.Date != end.Date)
            return (false, "Error: time slot must be on a single day", new());

        string formattedStart = start.ToString(DateFormat);
        string formattedEnd = end.ToString(DateFormat);

        var allRooms = _db.GetAllRooms();
        var freeRooms = new List<Room>();

        foreach (var room in allRooms)
        {
            if (room.Capacity < minCapacity)
                continue;

            var clash = _db.FindClashingBooking(room.Id, formattedStart, formattedEnd);
            if (clash == null)
            {
                freeRooms.Add(room);
            }
        }

        // Smallest capacity first; then by room name
        freeRooms = freeRooms.OrderBy(r => r.Capacity).ThenBy(r => r.Name).ToList();
        return (true, string.Empty, freeRooms);
    }

    public (bool Success, string Message, List<Booking> Bookings) GetRoomSchedule(string roomName, string dateStr)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            return (false, "Error: room name cannot be empty", new());

        var room = _db.GetRoomByName(roomName.Trim());
        if (room == null)
            return (false, $"Error: room {roomName} does not exist", new());

        if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (false, $"Error: invalid date format '{dateStr}'. Expected YYYY-MM-DD", new());

        string datePrefix = date.ToString("yyyy-MM-dd");
        var bookings = _db.GetActiveBookingsForRoomOnDate(room.Id, datePrefix);
        return (true, string.Empty, bookings);
    }

    public (int SuccessCount, int FailureCount, bool RuleHolds, string Summary) RunSimulation(string targetDate = "2026-11-01")
    {
        const string roomName = "R3";
        const int threadCount = 20;

        // Ensure R3 exists
        var room = _db.GetRoomByName(roomName);
        if (room == null)
            return (0, 0, false, $"Error: target room {roomName} does not exist");

        // Cancel any active bookings on that day for R3 to start clean
        var existing = _db.GetActiveBookingsForRoomOnDate(room.Id, targetDate);
        foreach (var b in existing)
        {
            _db.CancelBooking(b.Id);
        }

        var results = new (int ThreadId, string UserId, bool Success, string Message)[threadCount];
        var threads = new Thread[threadCount];

        // Use a barrier / countdown to release all 20 threads simultaneously
        using var readyBarrier = new ManualResetEventSlim(false);

        // 20 diverse slots that all overlap 10:00-11:00 on targetDate AND mutually clash with each other
        // Each slot satisfies start <= 10:30 and end >= 10:45
        var slotOffsets = new (int startOffsetMin, int durationMin)[]
        {
            (0, 60),    // 10:00 - 11:00
            (15, 60),   // 10:15 - 11:15
            (30, 60),   // 10:30 - 11:30
            (-15, 60),  // 09:45 - 10:45
            (-30, 75),  // 09:30 - 10:45
            (0, 75),    // 10:00 - 11:15
            (15, 45),   // 10:15 - 11:00
            (30, 30),   // 10:30 - 11:00
            (0, 90),    // 10:00 - 11:30
            (15, 90),   // 10:15 - 11:45
            (-15, 90),  // 09:45 - 11:15
            (0, 120),   // 10:00 - 12:00
            (-30, 120), // 09:30 - 11:30
            (0, 45),    // 10:00 - 10:45
            (15, 30),   // 10:15 - 10:45
            (30, 45),   // 10:30 - 11:15
            (-45, 90),  // 09:15 - 10:45
            (0, 105),   // 10:00 - 11:45
            (15, 75),   // 10:15 - 11:30
            (30, 75)    // 10:30 - 11:45
        };

        var baseTime = DateTime.ParseExact($"{targetDate} 10:00", DateFormat, CultureInfo.InvariantCulture);

        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            string userId = $"sim_user_{index + 1}";
            var (startOffset, duration) = slotOffsets[index % slotOffsets.Length];
            var slotStart = baseTime.AddMinutes(startOffset).ToString(DateFormat);
            var slotEnd = baseTime.AddMinutes(startOffset + duration).ToString(DateFormat);

            threads[index] = new Thread(() =>
            {
                readyBarrier.Wait(); // Wait until all threads are created and ready
                var res = BookRoom(roomName, userId, slotStart, slotEnd);
                results[index] = (index + 1, userId, res.Success, res.Message);
            });
            threads[index].Start();
        }

        // Fire all threads at the exact same instant
        readyBarrier.Set();

        // Wait for all 20 threads to complete
        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Join();
        }

        int successCount = results.Count(r => r.Success);
        int failureCount = results.Count(r => !r.Success);

        // Verification check:
        // Rule: Exactly 1 thread succeeds, 19 fail with a clash, and R3 has one active booking in that window
        var finalBookings = _db.GetActiveBookingsForRoomOnDate(room.Id, targetDate);
        bool ruleHolds = successCount == 1 && failureCount == 19 && finalBookings.Count == 1;

        string summary = $"Simulate Finished:\n" +
                         $"  - Threads Run: {threadCount}\n" +
                         $"  - Succeeded: {successCount}\n" +
                         $"  - Failed: {failureCount}\n" +
                         $"  - Total Active Bookings in R3: {finalBookings.Count}\n" +
                         $"  - Threading Rule Holds: {(ruleHolds ? "YES (PASSED)" : "NO (FAILED)")}";

        return (successCount, failureCount, ruleHolds, summary);
    }
}
