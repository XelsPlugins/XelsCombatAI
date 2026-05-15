using System.Globalization;

namespace FightReview;

internal static class SafetyRasterCodec
{
    public const int Safe = 0;
    public const int Blocked = 1;
    public const int ActiveDanger = 2;
    public const int FutureDanger = 3;
    public const int AvoidBuffer = 4;
    public const int Goal = 5;

    public static string Encode(IReadOnlyList<int> cells)
    {
        if (cells.Count == 0)
        {
            return string.Empty;
        }

        var runs = new List<string>();
        var current = cells[0];
        var count = 1;
        for (var i = 1; i < cells.Count; i++)
        {
            if (cells[i] == current)
            {
                count++;
                continue;
            }

            runs.Add(FormattableString.Invariant($"{current}:{count}"));
            current = cells[i];
            count = 1;
        }

        runs.Add(FormattableString.Invariant($"{current}:{count}"));
        return string.Join(",", runs);
    }

    public static int[] Decode(string rle, int expectedCount)
    {
        if (expectedCount <= 0)
        {
            return [];
        }

        var cells = new List<int>(expectedCount);
        if (!string.IsNullOrWhiteSpace(rle))
        {
            foreach (var run in rle.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = run.Split(':', 2);
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var state) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                    count < 0)
                {
                    throw new InvalidDataException($"Invalid safety raster RLE run '{run}'.");
                }

                for (var i = 0; i < count && cells.Count < expectedCount; i++)
                {
                    cells.Add(state);
                }
            }
        }

        while (cells.Count < expectedCount)
        {
            cells.Add(Safe);
        }

        return cells.ToArray();
    }

    public static bool IsBlockedOrDanger(string state)
    {
        return state is "blocked" or "active-danger" or "future-danger";
    }

    public static bool IsHardBlocked(string state)
    {
        return state is "blocked" or "active-danger";
    }

    public static bool IsSafeForMovement(string state)
    {
        return state is "safe" or "avoid-buffer" or "goal";
    }
}
