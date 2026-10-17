# Meeting Room Booking System

A command-line meeting room booking system written in **C# (.NET 8)** adhering to zero-framework, zero-ORM constraints. It persists all state in **SQLite** via raw SQL queries using the official `Microsoft.Data.Sqlite` driver and guarantees zero double-bookings under concurrent access.

---

## Architecture & Layers

The project follows a clean 3-layer architecture:
- **Presentation Layer (`Program.cs`)**: Handles input parsing, command dispatching, interactive REPL loop, and top-level error trapping so the application never crashes or outputs unhandled stack traces.
- **Business Logic Layer (`BookingService.cs`)**: Validates time slot steps (15-min intervals), duration boundaries (15m to 4h), same-day constraints, user daily quota (max 3 active bookings/day), and coordinates the 20-thread concurrency simulation.
- **Data Access Layer (`Database.cs`)**: Pure ADO.NET with raw SQL queries, schema initialization, room seeding, and atomic SQLite transactions with synchronization locks.

---

## Engineering Principles & Technical Concepts Applied

Here is a summary of software engineering best practices and architectural patterns applied across this solution:

### 1. Security & Data Integrity
- **SQL Injection Prevention**: All database commands strictly use **parameterized queries** (`cmd.Parameters.AddWithValue`), ensuring user input is treated as literal data, never executable SQL.
- **Input Sanitization & Normalization**: Inputs are trimmed and normalized with case-insensitive matching (`UPPER(user_id)`, `UPPER(room_name)`).

### 2. Concurrency & Multi-Threading
- **Race Condition Mitigation**: Applied critical-section mutual exclusion (`lock (_dbLock)`) around booking writes to eliminate double-booking risks under concurrent requests.
- **Barrier Synchronization (`ManualResetEventSlim`)**: Used as a starting gun in the simulation to release 20 worker threads at the exact same millisecond, reproducing real-world high contention.
- **Eliminating `SQLITE_BUSY`**: Serialized write operations in memory prevent SQLite file-lock conflicts and connection starvation.

### 3. Database Engineering & ACID Compliance
- **Atomicity & Consistency (ACID)**: Wrapped booking checks and inserts inside explicit database transactions (`BeginTransaction()` and `Commit()`) ensuring atomic state transitions.
- **B-Tree Composite Indexing**: Added composite indexes (`idx_bookings_room_status` on `(room_id, status, start_time, end_time)`) for fast $\mathcal{O}(\log N)$ interval clash searches.
- **Connection Lifecycle Management**: Enforced strict resource disposal via the C# `using` pattern (`IDisposable`), preventing connection leaks and file-handle exhaustion.
- **Zero-ORM Raw SQL**: Wrote raw, hand-crafted SQL DDL and DML using standard ADO.NET (`Microsoft.Data.Sqlite`) with zero code generation overhead.

### 4. Mathematical Interval Logic
- **Boundary Interval Theorem**: Implemented non-overlapping condition `newStart < existingEnd AND newEnd > existingStart`. Touching bookings where `newStart == existingEnd` evaluate to false, correctly allowing back-to-back meetings.
- **Discrete Time Discretization**: Enforced 15-minute slot quantum (`minute % 15 == 0`, `seconds == 0`) and duration bounding (15m to 4h on same calendar date).



---

## Database Schema

```sql
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
    status TEXT NOT NULL, -- 'ACTIVE' or 'CANCELLED'
    created_at TEXT NOT NULL,
    FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX IF NOT EXISTS idx_bookings_room_status 
ON bookings(room_id, status, start_time, end_time);

CREATE INDEX IF NOT EXISTS idx_bookings_user_status 
ON bookings(user_id, status, start_time);
```

### Initial Seed Data
On first run, the database automatically seeds 6 rooms if not present:
- `R1`: capacity 4
- `R2`: capacity 4
- `R3`: capacity 6
- `R4`: capacity 8
- `R5`: capacity 12
- `R6`: capacity 20

---

## How to Run

### Requirements
- [.NET 8 SDK](https://dotnet.microsoft.com/)

### 1. Interactive Mode
Run without arguments to enter the interactive REPL:
```bash
dotnet run --project MeetingRoomBooking
```

### 2. Single Command Mode
Run commands directly from the shell:
```bash
dotnet run --project MeetingRoomBooking -- <command> [arguments...]
```

---

## Commands & Example Runs

### 1. `book <room> <user> <start> <end>`
Books a room. Fails if it overlaps an active booking in that room, and states the clashing booking ID.
```bash
dotnet run --project MeetingRoomBooking -- book R1 Alice 2026-11-01 10:00 2026-11-01 11:00
# Output: Booking 1 confirmed for room R1 from 2026-11-01 10:00 to 2026-11-01 11:00

# Touching bookings are allowed (starts exactly when previous ends):
dotnet run --project MeetingRoomBooking -- book R1 Bob 2026-11-01 11:00 2026-11-01 12:00
# Output: Booking 2 confirmed for room R1 from 2026-11-01 11:00 to 2026-11-01 12:00

# Overlapping booking fails and reports clashing ID:
dotnet run --project MeetingRoomBooking -- book R1 Charlie 2026-11-01 10:30 2026-11-01 11:30
# Output: Error: clashes with active booking 1 (2026-11-01 10:00 - 2026-11-01 11:00)
```

### 2. `cancel <booking_id>`
Cancels an active booking; the time slot immediately becomes free.
```bash
dotnet run --project MeetingRoomBooking -- cancel 1
# Output: Booking 1 cancelled; slot is now free
```

### 3. `free <start> <end> [--min-capacity <n>]`
Lists all rooms free for the whole requested slot, sorted by smallest capacity first.
```bash
dotnet run --project MeetingRoomBooking -- free 2026-11-01 10:00 2026-11-01 11:00 --min-capacity 6
# Output:
# Free rooms (4):
#   - R3 (capacity: 6)
#   - R4 (capacity: 8)
#   - R5 (capacity: 12)
#   - R6 (capacity: 20)
```

### 4. `show <room> <date>`
Lists all active bookings for that room on the specified date, sorted by start time.
```bash
dotnet run --project MeetingRoomBooking -- show R1 2026-11-01
# Output:
# Bookings for R1 on 2026-11-01 (1):
#   - Booking #2: User Bob | 2026-11-01 11:00 to 12:00 (ACTIVE)
```

### 5. `simulate`
Spawns 20 threads simultaneously trying to book room R3 for overlapping slots.
```bash
dotnet run --project MeetingRoomBooking -- simulate
# Output:
# Running 20-thread concurrency simulation for room R3...
# Simulate Finished:
#   - Threads Run: 20
#   - Succeeded: 1
#   - Failed: 19
#   - Total Active Bookings in R3: 1
#   - Threading Rule Holds: YES (PASSED)
```

---

## How Thread Safety is Kept Correct (5–10 Lines)

1. The critical booking section (`TryCreateBooking`) synchronizes concurrent threads using a static synchronization gate combined with an atomic SQLite transaction (`BeginTransaction`).
2. A `ManualResetEventSlim` holds all 20 threads until they are all initialized, releasing them concurrently at the exact same instant to simulate high-load race conditions.
3. Overlap detection follows the strictly validated rule: `start < existing.end AND end > existing.start`. Touching intervals (`A.end == B.start`) evaluate to `false`, correctly allowing back-to-back bookings.
4. Because the overlap check and the `INSERT` statement are executed together within the synchronized atomic boundary, the first thread commits its booking immediately.
5. All subsequent 19 threads immediately detect the committed booking as an active clash, extract the winner's booking ID, and cleanly return failure without deadlocks or double-bookings.

---

## Automated Tests

Run the test suite using `dotnet test`:
```bash
dotnet test
```

### Test Suite Summary:
1. `Test_1_BookingSuccess_And_TouchingBookingsAllowed`: Confirms a booking and verifies that a touching slot (e.g. 10:00-10:30 and 10:30-11:00) is allowed.
2. `Test_2_OverlappingBooking_Fails_And_Reports_ClashId`: Verifies overlapping bookings fail and return the clashing booking ID.
3. `Test_3_CancelBooking_FreesTheSlot`: Verifies slot becomes available immediately after cancellation.
4. `Test_4_FreeRooms_FilteredAndSortedByCapacityAscending`: Tests filtering by minimum capacity and sorting by smallest capacity first.
5. `Test_5_User_CannotExceed_ThreeActiveBookings_OnSameDay`: Enforces the maximum 3 active bookings per user per day rule.
6. `Test_6_Simulate_TwentyThreads_ExactlyOneSucceeds_NineteenFail`: Concurrency test verifying that exactly 1 out of 20 concurrent threads succeeds and 19 fail with a clash.

---

## What I Would Do With Another Day

1. **User Authentication & Roles**: Add JWT / API keys or simple password authentication, distinguishing between standard users and room administrators.

2. **Recurring Bookings**: Support recurring series (e.g. every Monday 10:00-11:00 for N weeks) with transactional batch rollback if any slot in the series clashes.

3. **Room Features with applyingfilter while someone booking rooms**: Allow filtering by amenities (e.g., projector, video conference equipment, whiteboard).

4. **REST API & Web UI**: Wrap the core service in an ASP.NET Core Minimal API with a clean web dashboard showing visual daily room timelines.

5. **Suggest as per available roomswhen user's booking clashes current holded room**: When a booking fails due to a clash, calculate and suggest the nearest available 15-minute time slots or alternative rooms with sufficient capacity.
