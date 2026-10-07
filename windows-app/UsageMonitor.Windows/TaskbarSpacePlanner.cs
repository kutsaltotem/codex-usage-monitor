namespace CodexUsageMonitor.Windows;

internal static class TaskbarSpacePlanner
{
    internal sealed record Placement(int Left, int Width, int Density);
    internal static Placement? Find(int left, int right, int top, int bottom, int gap, double scale,
        IEnumerable<int[]> obstacles, int previousDensity = 0)
    {
        var slots = new List<(int Left, int Right)>();
        var cursor = left;
        foreach (var obstacle in obstacles.Where(r => r[1] < bottom && r[3] > top).OrderBy(r => r[0]))
        {
            var start = Math.Clamp(obstacle[0] - gap, left, right);
            var end = Math.Clamp(obstacle[2] + gap, left, right);
            if (start > cursor) slots.Add((cursor, start));
            cursor = Math.Max(cursor, end);
        }
        if (cursor < right) slots.Add((cursor, right));
        for (var density = 0; density < 3; density++)
        {
            var width = (int)Math.Ceiling((density == 0 ? 350 : density == 1 ? 104 : 34) * scale);
            foreach (var slot in slots.OrderByDescending(slot => slot.Right))
                // Shrink immediately, but require clearance before expanding again.
                // This avoids toggling around a boundary during tray animations.
                if (slot.Right - slot.Left >= width + (density < previousDensity ? (int)Math.Ceiling(16 * scale) : 0))
                    return new(slot.Right - width, width, density);
        }
        return null;
    }
}
