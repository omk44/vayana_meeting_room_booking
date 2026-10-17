using Microsoft.Data.Sqlite;
using MeetingRoomBooking.Models;

namespace MeetingRoomBooking.Data;

public class Database
{
    private readonly string _connectionString;
    private static readonly object _dbLock = new();

    public Database(string dbPath = "meeting_rooms.db")
    {
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();

            // Create tables using raw SQL
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS rooms (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL UNIQUE,
                        capacity INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS bookings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        room_id INTEGER NOT NULL,
                        user_id TEXT NOT NULL,
                        start_time TEXT NOT NULL,
                        end_time TEXT NOT NULL,
                        status TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY (room_id) REFERENCES rooms(id)
                    );

                    CREATE INDEX IF NOT EXISTS idx_bookings_room_status 
                    ON bookings(room_id, status, start_time, end_time);

                    CREATE INDEX IF NOT EXISTS idx_bookings_user_status 
                    ON bookings(user_id, status, start_time);
                ";
                cmd.ExecuteNonQuery();
            }

            // Seed initial rooms: R1 to R6 if empty
            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM rooms;";
                long count = (long)(countCmd.ExecuteScalar() ?? 0L);

                if (count == 0)
                {
                    var initialRooms = new (string name, int capacity)[]
                    {
                        ("R1", 4),
                        ("R2", 4),
                        ("R3", 6),
                        ("R4", 8),
                        ("R5", 12),
                        ("R6", 20)
                    };

                    using var transaction = connection.BeginTransaction();
                    foreach (var (name, capacity) in initialRooms)
                    {
                        using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = @"
                            INSERT INTO rooms (name, capacity)
                            VALUES (@name, @capacity);
                        ";
                        insertCmd.Parameters.AddWithValue("@name", name);
                        insertCmd.Parameters.AddWithValue("@capacity", capacity);
                        insertCmd.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
            }
        }
    }

    public List<Room> GetAllRooms()
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, capacity FROM rooms ORDER BY id;";

            using var reader = cmd.ExecuteReader();
            var list = new List<Room>();
            while (reader.Read())
            {
                list.Add(new Room
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Capacity = reader.GetInt32(2)
                });
            }
            return list;
        }
    }

    public Room? GetRoomByName(string name)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, capacity FROM rooms WHERE UPPER(name) = UPPER(@name);";
            cmd.Parameters.AddWithValue("@name", name);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new Room
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Capacity = reader.GetInt32(2)
                };
            }
            return null;
        }
    }

    public Room? GetRoomById(int roomId)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, capacity FROM rooms WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", roomId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new Room
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Capacity = reader.GetInt32(2)
                };
            }
            return null;
        }
    }

    public Booking? GetBookingById(int bookingId)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, room_id, user_id, start_time, end_time, status, created_at
                FROM bookings
                WHERE id = @id;
            ";
            cmd.Parameters.AddWithValue("@id", bookingId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadBooking(reader);
            }
            return null;
        }
    }

    public bool CancelBooking(int bookingId)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE bookings 
                SET status = 'CANCELLED' 
                WHERE id = @id AND status = 'ACTIVE';
            ";
            cmd.Parameters.AddWithValue("@id", bookingId);
            int rowsAffected = cmd.ExecuteNonQuery();
            return rowsAffected > 0;
        }
    }

    public List<Booking> GetActiveBookingsForRoomOnDate(int roomId, string datePrefix)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, room_id, user_id, start_time, end_time, status, created_at
                FROM bookings
                WHERE room_id = @roomId 
                  AND status = 'ACTIVE' 
                  AND start_time LIKE @datePrefix || '%'
                ORDER BY start_time ASC;
            ";
            cmd.Parameters.AddWithValue("@roomId", roomId);
            cmd.Parameters.AddWithValue("@datePrefix", datePrefix);

            using var reader = cmd.ExecuteReader();
            var list = new List<Booking>();
            while (reader.Read())
            {
                list.Add(ReadBooking(reader));
            }
            return list;
        }
    }

    public int GetActiveBookingCountForUserOnDate(string userId, string datePrefix)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM bookings
                WHERE UPPER(user_id) = UPPER(@userId)
                  AND status = 'ACTIVE'
                  AND start_time LIKE @datePrefix || '%';
            ";
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@datePrefix", datePrefix);

            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    public Booking? FindClashingBooking(int roomId, string startStr, string endStr)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            // Two intervals [A_start, A_end) and [B_start, B_end) overlap if:
            // A_start < B_end AND A_end > B_start.
            // Touching bookings (e.g. 10:00-10:30 and 10:30-11:00) have A_end == B_start, so no overlap!
            cmd.CommandText = @"
                SELECT id, room_id, user_id, start_time, end_time, status, created_at
                FROM bookings
                WHERE room_id = @roomId
                  AND status = 'ACTIVE'
                  AND start_time < @newEnd
                  AND end_time > @newStart
                ORDER BY start_time ASC
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("@roomId", roomId);
            cmd.Parameters.AddWithValue("@newStart", startStr);
            cmd.Parameters.AddWithValue("@newEnd", endStr);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadBooking(reader);
            }
            return null;
        }
    }

    public (bool Success, Booking? CreatedBooking, Booking? ClashingBooking, string? ErrorMessage) TryCreateBooking(
        int roomId, string userId, string startStr, string endStr, string datePrefix)
    {
        lock (_dbLock)
        {
            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            // 1. Check user active bookings count for the day
            using (var userCountCmd = connection.CreateCommand())
            {
                userCountCmd.Transaction = transaction;
                userCountCmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM bookings
                    WHERE UPPER(user_id) = UPPER(@userId)
                      AND status = 'ACTIVE'
                      AND start_time LIKE @datePrefix || '%';
                ";
                userCountCmd.Parameters.AddWithValue("@userId", userId);
                userCountCmd.Parameters.AddWithValue("@datePrefix", datePrefix);

                int count = Convert.ToInt32(userCountCmd.ExecuteScalar() ?? 0);
                if (count >= 3)
                {
                    return (false, null, null, $"User '{userId}' already has 3 active bookings on this day.");
                }
            }

            // 2. Check for clashing booking in that room
            using (var clashCmd = connection.CreateCommand())
            {
                clashCmd.Transaction = transaction;
                clashCmd.CommandText = @"
                    SELECT id, room_id, user_id, start_time, end_time, status, created_at
                    FROM bookings
                    WHERE room_id = @roomId
                      AND status = 'ACTIVE'
                      AND start_time < @newEnd
                      AND end_time > @newStart
                    ORDER BY start_time ASC
                    LIMIT 1;
                ";
                clashCmd.Parameters.AddWithValue("@roomId", roomId);
                clashCmd.Parameters.AddWithValue("@newStart", startStr);
                clashCmd.Parameters.AddWithValue("@newEnd", endStr);

                using var reader = clashCmd.ExecuteReader();
                if (reader.Read())
                {
                    var clash = ReadBooking(reader);
                    return (false, null, clash, null);
                }
            }

            // 3. Insert new booking
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO bookings (room_id, user_id, start_time, end_time, status, created_at)
                    VALUES (@roomId, @userId, @start, @end, 'ACTIVE', @now);
                    SELECT last_insert_rowid();
                ";
                insertCmd.Parameters.AddWithValue("@roomId", roomId);
                insertCmd.Parameters.AddWithValue("@userId", userId);
                insertCmd.Parameters.AddWithValue("@start", startStr);
                insertCmd.Parameters.AddWithValue("@end", endStr);
                insertCmd.Parameters.AddWithValue("@now", now);

                long newId = (long)(insertCmd.ExecuteScalar() ?? 0L);
                transaction.Commit();

                var newBooking = new Booking
                {
                    Id = (int)newId,
                    RoomId = roomId,
                    UserId = userId,
                    Start = DateTime.Parse(startStr),
                    End = DateTime.Parse(endStr),
                    Status = "ACTIVE",
                    CreatedAt = DateTime.Parse(now)
                };

                return (true, newBooking, null, null);
            }
        }
    }

    private static Booking ReadBooking(SqliteDataReader reader)
    {
        return new Booking
        {
            Id = reader.GetInt32(0),
            RoomId = reader.GetInt32(1),
            UserId = reader.GetString(2),
            Start = DateTime.Parse(reader.GetString(3)),
            End = DateTime.Parse(reader.GetString(4)),
            Status = reader.GetString(5),
            CreatedAt = DateTime.Parse(reader.GetString(6))
        };
    }
}
