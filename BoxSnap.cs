using System;
using System.Collections.Generic;
using System.Drawing;

namespace SwordBox;

[Flags]
internal enum BoxEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

internal static class BoxSnap
{
    public static Rectangle Bounds(Rectangle proposed, Rectangle workArea, IReadOnlyList<Rectangle> neighbors,
        int dpi, BoxEdge resizing = BoxEdge.None, Size minimum = default)
    {
        int gap = (8 * dpi + 48) / 96;
        int range = (12 * dpi + 48) / 96;
        bool moving = resizing == BoxEdge.None;
        bool left = moving || (resizing & BoxEdge.Left) != 0;
        bool right = moving || (resizing & BoxEdge.Right) != 0;
        bool top = moving || (resizing & BoxEdge.Top) != 0;
        bool bottom = moving || (resizing & BoxEdge.Bottom) != 0;
        int dx = SnapAxis(proposed, workArea, neighbors, gap, range, left, right, minimum.Width, horizontal: true);
        int dy = SnapAxis(proposed, workArea, neighbors, gap, range, top, bottom, minimum.Height, horizontal: false);
        return Rectangle.FromLTRB(proposed.Left + (left ? dx : 0), proposed.Top + (top ? dy : 0),
            proposed.Right + (right ? dx : 0), proposed.Bottom + (bottom ? dy : 0));
    }

    private static int SnapAxis(Rectangle proposed, Rectangle workArea, IReadOnlyList<Rectangle> neighbors,
        int gap, int range, bool nearEnabled, bool farEnabled, int minimum, bool horizontal)
    {
        if (!nearEnabled && !farEnabled) return 0;
        int near = horizontal ? proposed.Left : proposed.Top;
        int far = horizontal ? proposed.Right : proposed.Bottom;
        int crossNear = horizontal ? proposed.Top : proposed.Left;
        int crossFar = horizontal ? proposed.Bottom : proposed.Right;
        int workNear = horizontal ? workArea.Left : workArea.Top;
        int workFar = horizontal ? workArea.Right : workArea.Bottom;
        int best = 0, bestDistance = range + 1;
        bool protectsGap = false;

        if (nearEnabled) Consider(workNear + gap - near, near < workNear + gap);
        if (farEnabled) Consider(workFar - gap - far, far > workFar - gap);

        foreach (var other in neighbors)
        {
            int otherNear = horizontal ? other.Left : other.Top;
            int otherFar = horizontal ? other.Right : other.Bottom;
            int otherCrossNear = horizontal ? other.Top : other.Left;
            int otherCrossFar = horizontal ? other.Bottom : other.Right;
            int crossGap = Math.Max(otherCrossNear - crossFar, crossNear - otherCrossFar);
            if (crossGap > gap + range) continue;

            if (farEnabled)
            {
                int beforeGap = otherNear - far;
                Consider(beforeGap - gap, crossGap <= 0 && beforeGap <= gap);
            }
            if (nearEnabled)
            {
                int afterGap = near - otherFar;
                Consider(gap - afterGap, crossGap <= 0 && afterGap <= gap);
            }

            // Corresponding edges align when the boxes are near each other on the other axis.
            if (crossGap >= gap - range)
            {
                if (nearEnabled) Consider(otherNear - near, false);
                if (farEnabled) Consider(otherFar - far, false);
            }
        }
        return best;

        void Consider(int offset, bool spacing)
        {
            int distance = Math.Abs(offset);
            if (distance > range || (protectsGap && !spacing)) return;
            if (spacing == protectsGap && distance >= bestDistance) return;
            if (nearEnabled != farEnabled && far - near + (farEnabled ? offset : -offset) < minimum) return;
            best = offset;
            bestDistance = distance;
            protectsGap = spacing;
        }
    }
}
