using MeetingRoomBooking.Data;
using MeetingRoomBooking.Services;
using Xunit;

namespace MeetingRoomBooking.Tests;

public class BookingServiceTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly Database _database;
    private readonly BookingService _service;

    public BookingServiceTests()
    {
        _testDbPath = $"test_{Guid.NewGuid():N}.db";
        _database = new Database(_testDbPath);
        _service = new BookingService(_database);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch
        {
            // Ignore cleanup lock issues on test completion
        }
    }

    [Fact]
    public void Test_1_BookingSuccess_And_TouchingBookingsAllowed()
    {
        // 10:00 - 10:30
        var res1 = _service.BookRoom("R1", "User1", "2026-11-05 10:00", "2026-11-05 10:30");
        Assert.True(res1.Success);
        Assert.NotNull(res1.BookingId);

        // Touching slot: 10:30 - 11:00 (Must be allowed per problem rules)
        var res2 = _service.BookRoom("R1", "User2", "2026-11-05 10:30", "2026-11-05 11:00");
        Assert.True(res2.Success);
        Assert.NotNull(res2.BookingId);
        Assert.NotEqual(res1.BookingId, res2.BookingId);
    }

    [Fact]
    public void Test_2_OverlappingBooking_Fails_And_Reports_ClashId()
    {
        // Initial booking: 10:00 - 11:00
        var initial = _service.BookRoom("R2", "Alice", "2026-11-05 10:00", "2026-11-05 11:00");
        Assert.True(initial.Success);
        int initialId = initial.BookingId!.Value;

        // Overlapping booking: 10:45 - 11:30
        var overlap = _service.BookRoom("R2", "Bob", "2026-11-05 10:45", "2026-11-05 11:30");
        Assert.False(overlap.Success);
        Assert.Equal(initialId, overlap.ClashingBookingId);
        Assert.Contains($"clashes with active booking {initialId}", overlap.Message);
    }

    [Fact]
    public void Test_3_CancelBooking_FreesTheSlot()
    {
        // Book R4
        var bookRes = _service.BookRoom("R4", "Charlie", "2026-11-05 14:00", "2026-11-05 15:00");
        Assert.True(bookRes.Success);
        int bookingId = bookRes.BookingId!.Value;

        // Confirmed clash
        var clashRes = _service.BookRoom("R4", "Dave", "2026-11-05 14:00", "2026-11-05 15:00");
        Assert.False(clashRes.Success);

        // Cancel booking
        var cancelRes = _service.CancelBooking(bookingId.ToString());
        Assert.True(cancelRes.Success);

        // Now Dave can book the exact same slot
        var rebookRes = _service.BookRoom("R4", "Dave", "2026-11-05 14:00", "2026-11-05 15:00");
        Assert.True(rebookRes.Success);
        Assert.NotNull(rebookRes.BookingId);
    }

    [Fact]
    public void Test_4_FreeRooms_FilteredAndSortedByCapacityAscending()
    {
        // Book R1 (cap 4) and R3 (cap 6)
        _service.BookRoom("R1", "UserA", "2026-11-05 09:00", "2026-11-05 10:00");
        _service.BookRoom("R3", "UserB", "2026-11-05 09:00", "2026-11-05 10:00");

        // Query free rooms for this slot with min-capacity 6
        // R3 is booked, so remaining rooms >= 6 are R4 (8), R5 (12), R6 (20)
        var (success, _, rooms) = _service.GetFreeRooms("2026-11-05 09:00", "2026-11-05 10:00", minCapacity: 6);
        Assert.True(success);
        Assert.Equal(3, rooms.Count);

        // Check smallest capacity first
        Assert.Equal("R4", rooms[0].Name);
        Assert.Equal(8, rooms[0].Capacity);
        Assert.Equal("R5", rooms[1].Name);
        Assert.Equal(12, rooms[1].Capacity);
        Assert.Equal("R6", rooms[2].Name);
        Assert.Equal(20, rooms[2].Capacity);
    }

    [Fact]
    public void Test_5_User_CannotExceed_ThreeActiveBookings_OnSameDay()
    {
        string user = "PowerUser";
        string date = "2026-11-06";

        // Booking 1
        var b1 = _service.BookRoom("R1", user, $"{date} 09:00", $"{date} 10:00");
        Assert.True(b1.Success);

        // Booking 2
        var b2 = _service.BookRoom("R2", user, $"{date} 10:00", $"{date} 11:00");
        Assert.True(b2.Success);

        // Booking 3
        var b3 = _service.BookRoom("R3", user, $"{date} 11:00", $"{date} 12:00");
        Assert.True(b3.Success);

        // Booking 4 on same day by same user MUST be rejected
        var b4 = _service.BookRoom("R4", user, $"{date} 12:00", $"{date} 13:00");
        Assert.False(b4.Success);
        Assert.Contains("already has 3 active bookings on this day", b4.Message);

        // But on a DIFFERENT day, the user CAN book
        var nextDay = _service.BookRoom("R4", user, "2026-11-07 12:00", "2026-11-07 13:00");
        Assert.True(nextDay.Success);
    }

    [Fact]
    public void Test_6_Simulate_TwentyThreads_ExactlyOneSucceeds_NineteenFail()
    {
        // Runs the concurrency simulation
        var (successCount, failureCount, ruleHolds, _) = _service.RunSimulation("2026-11-10");

        Assert.Equal(1, successCount);
        Assert.Equal(19, failureCount);
        Assert.True(ruleHolds);
    }
}
