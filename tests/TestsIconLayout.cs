using System;
using System.Collections.Generic;
using System.Windows;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsIconLayout
    {
        private static Rect StandardArea()
        {
            return new Rect(0, 0, 1920, 1040);
        }

        private static Rect SecondaryArea()
        {
            // Second monitor to the right of a 1920px primary monitor.
            return new Rect(1920, 0, 1920, 1040);
        }

        private static DesktopProfile NewDesktop()
        {
            DesktopProfile desktop = new DesktopProfile();
            desktop.Id = "layout-desktop";
            desktop.Icons = new List<IconEntry>();
            return desktop;
        }

        private static IconEntry AddIcon(DesktopProfile desktop, double x, double y)
        {
            IconEntry icon = new IconEntry();
            icon.X = x;
            icon.Y = y;
            desktop.Icons.Add(icon);
            return icon;
        }

        [Test("IconLayout: ColumnsFor clamps to at least one column")]
        public static void ColumnsForBoundaries()
        {
            Assert.Equal(19, IconLayout.ColumnsFor(StandardArea()), "1920px wide area has 19 columns");
            Assert.Equal(19, IconLayout.ColumnsFor(SecondaryArea()), "second monitor area has 19 columns");
            Assert.Equal(3, IconLayout.ColumnsFor(new Rect(0, 0, 312, 300)), "312px fits 3 columns");
            Assert.Equal(2, IconLayout.ColumnsFor(new Rect(0, 0, 216, 300)), "216px fits 2 columns");
            Assert.Equal(1, IconLayout.ColumnsFor(new Rect(0, 0, 121, 300)), "121px fits 1 column");
            Assert.Equal(1, IconLayout.ColumnsFor(new Rect(0, 0, 120, 300)),
                "margin+cell width boundary still yields one column");
            Assert.Equal(1, IconLayout.ColumnsFor(new Rect(0, 0, 0, 0)), "zero sized area yields one column");
            Assert.Equal(1, IconLayout.ColumnsFor(new Rect(0, 0, 1, 1)), "1px area yields one column");
        }

        [Test("IconLayout: RowsFor clamps to at least one row")]
        public static void RowsForBoundaries()
        {
            Assert.Equal(9, IconLayout.RowsFor(StandardArea()), "1040px high area has 9 rows");
            Assert.Equal(3, IconLayout.RowsFor(new Rect(0, 0, 300, 328)), "328px fits 3 rows");
            Assert.Equal(2, IconLayout.RowsFor(new Rect(0, 0, 300, 224)), "224px fits 2 rows");
            Assert.Equal(1, IconLayout.RowsFor(new Rect(0, 0, 300, 121)), "121px fits 1 row");
            Assert.Equal(1, IconLayout.RowsFor(new Rect(0, 0, 300, 120)),
                "margin+cell height boundary still yields one row");
            Assert.Equal(1, IconLayout.RowsFor(new Rect(0, 0, 300, 0)), "zero sized area yields one row");
        }

        [Test("IconLayout: CellToPoint maps grid cells to pixels, including monitor offsets")]
        public static void CellToPointMapsGrid()
        {
            Point p = IconLayout.CellToPoint(StandardArea(), 0, 0);
            Assert.Near(24, p.X, 1e-9, "first cell X includes the margin");
            Assert.Near(16, p.Y, 1e-9, "first cell Y includes the margin");

            p = IconLayout.CellToPoint(StandardArea(), 2, 1);
            Assert.Near(216, p.X, 1e-9, "column 2 X");
            Assert.Near(120, p.Y, 1e-9, "row 1 Y");

            p = IconLayout.CellToPoint(StandardArea(), 18, 8);
            Assert.Near(1752, p.X, 1e-9, "last column X");
            Assert.Near(848, p.Y, 1e-9, "last row Y");

            p = IconLayout.CellToPoint(SecondaryArea(), 0, 0);
            Assert.Near(1944, p.X, 1e-9, "second monitor first cell is offset by the monitor origin");
            Assert.Near(16, p.Y, 1e-9, "second monitor row offset");

            p = IconLayout.CellToPoint(SecondaryArea(), 5, 2);
            Assert.Near(2424, p.X, 1e-9, "second monitor column 5 X");
            Assert.Near(224, p.Y, 1e-9, "second monitor row 2 Y");
        }

        [Test("IconLayout: PointToCell rounds to the nearest cell and clamps to the grid")]
        public static void PointToCellRoundsAndClamps()
        {
            Rect area = StandardArea();
            int col;
            int row;

            IconLayout.PointToCell(area, new Point(24, 16), out col, out row);
            Assert.Equal(0, col, "top-left point maps to column 0");
            Assert.Equal(0, row, "top-left point maps to row 0");

            IconLayout.PointToCell(area, new Point(120, 120), out col, out row);
            Assert.Equal(1, col, "120px maps to column 1");
            Assert.Equal(1, row, "120px maps to row 1");

            IconLayout.PointToCell(area, new Point(-500, -500), out col, out row);
            Assert.Equal(0, col, "negative X clamps to column 0");
            Assert.Equal(0, row, "negative Y clamps to row 0");

            IconLayout.PointToCell(area, new Point(99999, 99999), out col, out row);
            Assert.Equal(18, col, "far right point clamps to the last column");
            Assert.Equal(8, row, "far bottom point clamps to the last row");

            IconLayout.PointToCell(SecondaryArea(), new Point(1944, 16), out col, out row);
            Assert.Equal(0, col, "second monitor origin maps to column 0");
            Assert.Equal(0, row, "second monitor origin maps to row 0");

            IconLayout.PointToCell(SecondaryArea(), new Point(2424, 224), out col, out row);
            Assert.Equal(5, col, "second monitor point maps to column 5");
            Assert.Equal(2, row, "second monitor point maps to row 2");
        }

        [Test("IconLayout: ArrangeAll fills columns top-down (column major) and snaps to the grid")]
        public static void ArrangeAllUsesColumnMajorOrder()
        {
            DesktopProfile desktop = NewDesktop();
            for (int i = 0; i < 5; i++)
            {
                AddIcon(desktop, 9999, 9999);
            }
            IconLayout.ArrangeAll(desktop, new Rect(0, 0, 312, 328));

            Assert.Near(24, desktop.Icons[0].X, 1e-9, "icon 0 column 0 X");
            Assert.Near(16, desktop.Icons[0].Y, 1e-9, "icon 0 row 0 Y");
            Assert.Near(24, desktop.Icons[1].X, 1e-9, "icon 1 stays in column 0");
            Assert.Near(120, desktop.Icons[1].Y, 1e-9, "icon 1 goes to row 1");
            Assert.Near(24, desktop.Icons[2].X, 1e-9, "icon 2 stays in column 0");
            Assert.Near(224, desktop.Icons[2].Y, 1e-9, "icon 2 goes to row 2");
            Assert.Near(120, desktop.Icons[3].X, 1e-9, "icon 3 starts column 1");
            Assert.Near(16, desktop.Icons[3].Y, 1e-9, "icon 3 goes back to row 0");
            Assert.Near(120, desktop.Icons[4].X, 1e-9, "icon 4 in column 1");
            Assert.Near(120, desktop.Icons[4].Y, 1e-9, "icon 4 in row 1");
        }

        [Test("IconLayout: ArrangeAll and Normalize tolerate null/empty input")]
        public static void LayoutNullSafety()
        {
            IconLayout.ArrangeAll(null, StandardArea());
            IconLayout.Normalize(null, StandardArea());

            DesktopProfile desktop = NewDesktop();
            IconLayout.ArrangeAll(desktop, StandardArea());
            IconLayout.Normalize(desktop, StandardArea());
            Assert.Equal(0, desktop.Icons.Count, "empty desktop stays empty");

            desktop.Icons = null;
            IconLayout.ArrangeAll(desktop, StandardArea());
            IconLayout.Normalize(desktop, StandardArea());
            Assert.Null(desktop.Icons, "Normalize/ArrangeAll never create an icon list");
        }

        [Test("IconLayout: IsOccupied detects a cell and can exclude the moved icon")]
        public static void IsOccupiedDetectsCell()
        {
            DesktopProfile desktop = NewDesktop();
            IconEntry icon = AddIcon(desktop, 120, 120);
            Rect area = StandardArea();

            Assert.True(IconLayout.IsOccupied(desktop, null, 1, 1, area), "cell (1,1) is occupied");
            Assert.False(IconLayout.IsOccupied(desktop, null, 0, 1, area), "cell (0,1) is free");
            Assert.False(IconLayout.IsOccupied(desktop, icon.Id, 1, 1, area),
                "the excluded icon does not occupy its own cell");
            Assert.False(IconLayout.IsOccupied(null, null, 0, 0, area), "null desktop has no occupied cells");

            desktop.Icons.Add(null);
            Assert.False(IconLayout.IsOccupied(desktop, null, 5, 5, area), "null icons are skipped");
        }

        [Test("IconLayout: PlaceIcon snaps to the nearest free cell")]
        public static void PlaceIconSnapsToFreeCell()
        {
            DesktopProfile desktop = NewDesktop();
            Rect area = StandardArea();
            IconEntry icon = new IconEntry();
            Point placed = IconLayout.PlaceIcon(desktop, icon, area, new Point(100, 100));
            Assert.Near(120, placed.X, 1e-9, "desired point snaps to column 1");
            Assert.Near(120, placed.Y, 1e-9, "desired point snaps to row 1");
            Assert.Equal(120.0, placed.X, "snapped X is the exact grid coordinate");
            Assert.Equal(120.0, placed.Y, "snapped Y is the exact grid coordinate");
        }

        [Test("IconLayout: PlaceIcon avoids collisions with existing icons")]
        public static void PlaceIconAvoidsCollision()
        {
            DesktopProfile desktop = NewDesktop();
            Rect area = StandardArea();
            IconEntry first = AddIcon(desktop, 24, 16);
            IconEntry second = new IconEntry();

            Point placed = IconLayout.PlaceIcon(desktop, second, area, new Point(30, 20));
            int col;
            int row;
            IconLayout.PointToCell(area, placed, out col, out row);
            Assert.False(col == 0 && row == 0, "placed icon must not share the first cell");
            Assert.Near(24, placed.X, 1e-9, "nearest free cell is the cell right below (column-major layout)");
            Assert.Near(120, placed.Y, 1e-9, "nearest free cell is the cell right below (column-major layout)");

            int existingCol;
            int existingRow;
            IconLayout.PointToCell(area, new Point(first.X, first.Y), out existingCol, out existingRow);
            Assert.False(existingCol == col && existingRow == row, "placed icon shares no cell with an existing icon");
        }

        [Test("IconLayout: PlaceIcon overflows the grid when every cell is taken")]
        public static void PlaceIconOverflowWhenFull()
        {
            Rect tiny = new Rect(0, 0, 120, 120); // exactly one cell
            DesktopProfile desktop = NewDesktop();
            AddIcon(desktop, 24, 16);
            IconEntry second = new IconEntry();
            Point placed = IconLayout.PlaceIcon(desktop, second, tiny, new Point(30, 20));
            Assert.Near(120, placed.X, 1e-9, "full grid places the icon one column beyond the last cell");
            Assert.Near(16, placed.Y, 1e-9, "overflow row is clamped to the grid");
        }

        [Test("IconLayout: FindNearestFreeCell reports failure when the area is full")]
        public static void FindNearestFreeCellFullArea()
        {
            Rect tiny = new Rect(0, 0, 120, 120);
            DesktopProfile desktop = NewDesktop();
            AddIcon(desktop, 24, 16);
            int col;
            int row;
            bool found = IconLayout.FindNearestFreeCell(desktop, null, tiny, 0, 0, out col, out row);
            Assert.False(found, "no free cell remains");
            Assert.Equal(-1, col, "failure returns column -1");
            Assert.Equal(-1, row, "failure returns row -1");
        }

        [Test("IconLayout: NextFreeCell returns the first free cell, then spills right")]
        public static void NextFreeCellBehaviour()
        {
            Rect tiny = new Rect(0, 0, 120, 120);
            DesktopProfile desktop = NewDesktop();
            Point free = IconLayout.NextFreeCell(desktop, tiny);
            Assert.Near(24, free.X, 1e-9, "first free cell X");
            Assert.Near(16, free.Y, 1e-9, "first free cell Y");

            AddIcon(desktop, 24, 16);
            Point overflow = IconLayout.NextFreeCell(desktop, tiny);
            Assert.Near(120, overflow.X, 1e-9, "full grid spills one cell to the right");
            Assert.Near(16, overflow.Y, 1e-9, "spill keeps the last row Y");
        }

        [Test("IconLayout: Normalize repairs NaN, duplicate, out-of-range and off-grid positions")]
        public static void NormalizeRepairsPositions()
        {
            Rect area = StandardArea();
            DesktopProfile desktop = NewDesktop();

            IconEntry valid = AddIcon(desktop, 24, 16);
            IconEntry duplicate = AddIcon(desktop, 24, 16);
            IconEntry nan = AddIcon(desktop, double.NaN, double.NaN);
            IconEntry offGrid = AddIcon(desktop, 505, 225); // cell (5,2) with a 1px offset
            IconEntry outOfRange = AddIcon(desktop, 5000, 5000);

            IconLayout.Normalize(desktop, area);

            Assert.Near(24, valid.X, 1e-9, "first icon keeps its valid cell X");
            Assert.Near(16, valid.Y, 1e-9, "first icon keeps its valid cell Y");
            Assert.Near(504, offGrid.X, 1e-9, "off-grid icon is snapped to the grid X");
            Assert.Near(224, offGrid.Y, 1e-9, "off-grid icon is snapped to the grid Y");

            HashSet<string> cells = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry icon = desktop.Icons[i];
                Assert.NotNaN(icon.X, "normalized X for icon " + i + " must not be NaN");
                Assert.NotNaN(icon.Y, "normalized Y for icon " + i + " must not be NaN");
                Assert.InRange(icon.X, 24, 1752, "normalized X stays inside the grid area");
                Assert.InRange(icon.Y, 16, 848, "normalized Y stays inside the grid area");
                int col;
                int row;
                IconLayout.PointToCell(area, new Point(icon.X, icon.Y), out col, out row);
                cells.Add(col + "," + row);
            }
            Assert.Equal(desktop.Icons.Count, cells.Count, "no two icons share a grid cell after Normalize");
            Assert.NotSame(duplicate, nan, "sanity check on the fixture objects");
            Assert.True(outOfRange.X <= 1752, "out-of-range icon is brought back into the area");
        }

        [Test("IconLayout: Normalize assigns NaN icons to the first free cells in order")]
        public static void NormalizeAssignsNaNIcons()
        {
            Rect area = StandardArea();
            DesktopProfile desktop = NewDesktop();
            IconEntry nan = AddIcon(desktop, double.NaN, double.NaN);
            IconLayout.Normalize(desktop, area);
            Assert.Near(24, nan.X, 1e-9, "NaN icon gets the first free cell X");
            Assert.Near(16, nan.Y, 1e-9, "NaN icon gets the first free cell Y");
        }

        [Test("IconLayout: Normalize keeps icons inside an offset monitor area")]
        public static void NormalizeMultiMonitorArea()
        {
            Rect area = SecondaryArea();
            DesktopProfile desktop = NewDesktop();
            IconEntry icon = AddIcon(desktop, 100, 50); // belongs to the primary monitor / stale position
            IconLayout.Normalize(desktop, area);
            Assert.Near(1944, icon.X, 1e-9, "icon is moved into the offset monitor area X");
            Assert.Near(16, icon.Y, 1e-9, "icon is moved into the offset monitor area Y");
        }

        [Test("IconLayout: Normalize preserves a valid layout inside an offset monitor area")]
        public static void NormalizeKeepsValidOffsetLayout()
        {
            Rect area = SecondaryArea();
            DesktopProfile desktop = NewDesktop();
            IconEntry icon = AddIcon(desktop, 1944, 16);
            IconLayout.Normalize(desktop, area);
            Assert.Near(1944, icon.X, 1e-9, "valid offset X is preserved");
            Assert.Near(16, icon.Y, 1e-9, "valid offset Y is preserved");
        }
    }
}
