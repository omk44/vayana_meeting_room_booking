using MeetingRoomBooking.Data;
using MeetingRoomBooking.Services;

namespace MeetingRoomBooking;

public class Program
{
    public static void Main(string[] args)
    {
        var db = new Database();
        var service = new BookingService(db);

        if (args.Length > 0)
        {
            // Single command mode
            ExecuteCommand(string.Join(" ", args), service);
        }
        else
        {
            // Interactive REPL mode
            Console.WriteLine("=================================================");
            Console.WriteLine(" Meeting Room Booking CLI (Rooms R1-R6 Ready)    ");
            Console.WriteLine(" Type 'help' for commands, or 'exit' to quit.    ");
            Console.WriteLine("=================================================");

            while (true)
            {
                Console.Write("\n> ");
                string? input = Console.ReadLine();
                if (input == null || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                ExecuteCommand(input.Trim(), service);
            }
        }
    }

    public static void ExecuteCommand(string input, BookingService service)
    {
        try
        {
            var tokens = Tokenize(input);
            if (tokens.Count == 0) return;

            string command = tokens[0].ToLowerInvariant();

            switch (command)
            {
                case "book":
                    HandleBook(tokens, service);
                    break;
                case "cancel":
                    HandleCancel(tokens, service);
                    break;
                case "free":
                    HandleFree(tokens, service);
                    break;
                case "show":
                    HandleShow(tokens, service);
                    break;
                case "simulate":
                    HandleSimulate(service);
                    break;
                case "help":
                    PrintHelp();
                    break;
                default:
                    Console.WriteLine($"Error: unknown command '{command}'. Type 'help' for usage.");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Requirement: Never crash or print a stack trace. Always print a one-line error.
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void HandleBook(List<string> tokens, BookingService service)
    {
        // Expected formats:
        // book <room> <user> "YYYY-MM-DD HH:MM" "YYYY-MM-DD HH:MM" (4 arguments)
        // book <room> <user> YYYY-MM-DD HH:MM YYYY-MM-DD HH:MM (6 arguments)
        string room;
        string user;
        string startStr;
        string endStr;

        if (tokens.Count == 5)
        {
            room = tokens[1];
            user = tokens[2];
            startStr = tokens[3];
            endStr = tokens[4];
        }
        else if (tokens.Count == 7)
        {
            room = tokens[1];
            user = tokens[2];
            startStr = $"{tokens[3]} {tokens[4]}";
            endStr = $"{tokens[5]} {tokens[6]}";
        }
        else
        {
            Console.WriteLine("Error: invalid book command. Format: book <room> <user> <start> <end>");
            return;
        }

        var result = service.BookRoom(room, user, startStr, endStr);
        Console.WriteLine(result.Message);
    }

    private static void HandleCancel(List<string> tokens, BookingService service)
    {
        if (tokens.Count < 2)
        {
            Console.WriteLine("Error: missing booking id. Format: cancel <booking_id>");
            return;
        }

        var result = service.CancelBooking(tokens[1]);
        Console.WriteLine(result.Message);
    }

    private static void HandleFree(List<string> tokens, BookingService service)
    {
        // free <start> <end> [--min-capacity <n>]
        // Can be tokenized with quoted dates or unquoted dates:
        // Case A (quoted): free "2026-11-01 10:00" "2026-11-01 11:00" [--min-capacity 6]
        // Case B (unquoted): free 2026-11-01 10:00 2026-11-01 11:00 [--min-capacity 6]
        string startStr;
        string endStr;
        int minCapacity = 0;

        int flagIndex = tokens.FindIndex(t => t.Equals("--min-capacity", StringComparison.OrdinalIgnoreCase));
        if (flagIndex != -1)
        {
            if (flagIndex + 1 < tokens.Count && int.TryParse(tokens[flagIndex + 1], out int cap))
            {
                minCapacity = cap;
            }
            else
            {
                Console.WriteLine("Error: --min-capacity requires an integer value.");
                return;
            }
        }

        var nonFlagTokens = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (flagIndex != -1 && (i == flagIndex || i == flagIndex + 1)) continue;
            nonFlagTokens.Add(tokens[i]);
        }

        if (nonFlagTokens.Count == 3) // free "start" "end"
        {
            startStr = nonFlagTokens[1];
            endStr = nonFlagTokens[2];
        }
        else if (nonFlagTokens.Count == 5) // free YYYY-MM-DD HH:MM YYYY-MM-DD HH:MM
        {
            startStr = $"{nonFlagTokens[1]} {nonFlagTokens[2]}";
            endStr = $"{nonFlagTokens[3]} {nonFlagTokens[4]}";
        }
        else
        {
            Console.WriteLine("Error: invalid free command. Format: free <start> <end> [--min-capacity <n>]");
            return;
        }

        var (success, errorMsg, rooms) = service.GetFreeRooms(startStr, endStr, minCapacity);
        if (!success)
        {
            Console.WriteLine(errorMsg);
            return;
        }

        if (rooms.Count == 0)
        {
            Console.WriteLine("No rooms are free for that slot.");
        }
        else
        {
            Console.WriteLine($"Free rooms ({rooms.Count}):");
            foreach (var r in rooms)
            {
                Console.WriteLine($"  - {r.Name} (capacity: {r.Capacity})");
            }
        }
    }

    private static void HandleShow(List<string> tokens, BookingService service)
    {
        // show <room> <date>
        if (tokens.Count != 3)
        {
            Console.WriteLine("Error: invalid show command. Format: show <room> <YYYY-MM-DD>");
            return;
        }

        string roomName = tokens[1];
        string dateStr = tokens[2];

        var (success, errorMsg, bookings) = service.GetRoomSchedule(roomName, dateStr);
        if (!success)
        {
            Console.WriteLine(errorMsg);
            return;
        }

        if (bookings.Count == 0)
        {
            Console.WriteLine($"No active bookings for room {roomName.ToUpperInvariant()} on {dateStr}.");
        }
        else
        {
            Console.WriteLine($"Bookings for {roomName.ToUpperInvariant()} on {dateStr} ({bookings.Count}):");
            foreach (var b in bookings)
            {
                Console.WriteLine($"  - Booking #{b.Id}: User {b.UserId} | {b.Start:yyyy-MM-dd HH:mm} to {b.End:HH:mm} ({b.Status})");
            }
        }
    }

    private static void HandleSimulate(BookingService service)
    {
        Console.WriteLine("Running 20-thread concurrency simulation for room R3...");
        var (succeeded, failed, ruleHolds, summary) = service.RunSimulation();
        Console.WriteLine(summary);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
Available Commands:
  book <room> <user> <start> <end>
      Example: book R1 Alice 2026-11-01 10:00 2026-11-01 11:00
      Fails if time overlaps an active booking in the room.

  cancel <booking_id>
      Example: cancel 1
      Cancels the booking; frees up the time slot.

  free <start> <end> [--min-capacity <n>]
      Example: free 2026-11-01 10:00 2026-11-01 11:00 --min-capacity 6
      Lists rooms free for the slot, smallest capacity first.

  show <room> <date>
      Example: show R1 2026-11-01
      Lists active bookings for the room on that date, sorted by start time.

  simulate
      Runs 20 threads simultaneously trying to book R3 overlapping 10:00-11:00.
      Verifies exactly 1 succeeds and 19 fail with a clash.

  help
      Shows this help menu.

  exit / quit
      Exits the interactive program.
");
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (char c in commandLine)
        {
            if (c == '"' || c == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
