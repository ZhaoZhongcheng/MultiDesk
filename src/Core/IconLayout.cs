using System;
using System.Collections.Generic;
using System.Windows;

namespace MultiDesk.Core
{
    /// <summary>
    /// 一组图标栅格尺寸参数（图标大小 + 单元格大小 + 边距）。
    /// 允许用户在设置中选择小/中/大图标，所有排版逻辑都通过本对象计算。
    /// </summary>
    public sealed class IconMetrics
    {
        public readonly int IconSize;
        public readonly double CellWidth;
        public readonly double CellHeight;
        public readonly double MarginX;
        public readonly double MarginY;

        public IconMetrics(int iconSize, double cellWidth, double cellHeight, double marginX, double marginY)
        {
            IconSize = iconSize;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
            MarginX = marginX;
            MarginY = marginY;
        }
    }

    /// <summary>
    /// 桌面图标栅格布局工具（纯逻辑，可单元测试）。
    /// 坐标均为 WPF 逻辑像素（DIP），原点是虚拟屏幕左上角。
    /// 桌面图标按「先向下、再向右」的列优先顺序排列，与 Windows 桌面一致。
    /// 
    /// 无参数的旧方法使用默认 48px 图标的尺寸（单元格 96x104），保留用于兼容旧配置与单元测试；
    /// 新代码应通过 CreateMetrics(iconSize) + 带 IconMetrics 参数的重载来支持可调图标大小。
    /// </summary>
    public static class IconLayout
    {
        public const double CellWidth = 96.0;
        public const double CellHeight = 104.0;
        public const double MarginX = 24.0;
        public const double MarginY = 16.0;

        private static readonly IconMetrics DefaultMetrics = new IconMetrics(48, CellWidth, CellHeight, MarginX, MarginY);

        /// <summary>默认尺寸参数（48px 图标 / 96x104 单元格）。</summary>
        public static IconMetrics Default
        {
            get { return DefaultMetrics; }
        }

        /// <summary>由图标像素大小创建栅格参数：单元格 = 图标 + 48(宽) / +56(高)。</summary>
        public static IconMetrics CreateMetrics(int iconSize)
        {
            if (iconSize <= 0) iconSize = 48;
            if (iconSize < 24) iconSize = 24;
            if (iconSize > 96) iconSize = 96;
            return new IconMetrics(iconSize, iconSize + 48.0, iconSize + 56.0, MarginX, MarginY);
        }

        /// <summary>可用列数（至少 1 列）。</summary>
        public static int ColumnsFor(Rect area)
        {
            return ColumnsFor(area, DefaultMetrics);
        }

        public static int ColumnsFor(Rect area, IconMetrics metrics)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            if (area.Width <= m.MarginX + m.CellWidth) return 1;
            int n = (int)Math.Floor((area.Width - m.MarginX) / m.CellWidth);
            return Math.Max(1, n);
        }

        /// <summary>可用行数（至少 1 行）。</summary>
        public static int RowsFor(Rect area)
        {
            return RowsFor(area, DefaultMetrics);
        }

        public static int RowsFor(Rect area, IconMetrics metrics)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            if (area.Height <= m.MarginY + m.CellHeight) return 1;
            int n = (int)Math.Floor((area.Height - m.MarginY) / m.CellHeight);
            return Math.Max(1, n);
        }

        /// <summary>栅格坐标 → 图标左上角坐标。</summary>
        public static Point CellToPoint(Rect area, int col, int row)
        {
            return CellToPoint(area, col, row, DefaultMetrics);
        }

        public static Point CellToPoint(Rect area, int col, int row, IconMetrics metrics)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            return new Point(area.X + m.MarginX + col * m.CellWidth,
                             area.Y + m.MarginY + row * m.CellHeight);
        }

        /// <summary>图标左上角坐标 → 最近的栅格坐标（会夹在可用范围内）。</summary>
        public static void PointToCell(Rect area, Point p, out int col, out int row)
        {
            PointToCell(area, p, DefaultMetrics, out col, out row);
        }

        public static void PointToCell(Rect area, Point p, IconMetrics metrics, out int col, out int row)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            int maxCol = ColumnsFor(area, m) - 1;
            int maxRow = RowsFor(area, m) - 1;
            col = (int)Math.Round((p.X - area.X - m.MarginX) / m.CellWidth, MidpointRounding.AwayFromZero);
            row = (int)Math.Round((p.Y - area.Y - m.MarginY) / m.CellHeight, MidpointRounding.AwayFromZero);
            if (col < 0) col = 0;
            if (row < 0) row = 0;
            if (col > maxCol) col = maxCol;
            if (row > maxRow) row = maxRow;
        }

        /// <summary>把图标排列成列优先顺序（保留列表顺序）。</summary>
        public static void ArrangeAll(DesktopProfile desktop, Rect area)
        {
            ArrangeAll(desktop, area, DefaultMetrics);
        }

        public static void ArrangeAll(DesktopProfile desktop, Rect area, IconMetrics metrics)
        {
            if (desktop == null || desktop.Icons == null || desktop.Icons.Count == 0) return;
            IconMetrics m = metrics ?? DefaultMetrics;
            int maxRow = RowsFor(area, m) - 1;
            int index = 0;
            for (int col = 0; index < desktop.Icons.Count && col <= ColumnsFor(area, m) - 1; col++)
            {
                for (int row = 0; row <= maxRow && index < desktop.Icons.Count; row++)
                {
                    Point p = CellToPoint(area, col, row, m);
                    desktop.Icons[index].X = p.X;
                    desktop.Icons[index].Y = p.Y;
                    index++;
                }
            }
            while (index < desktop.Icons.Count)
            {
                Point last = CellToPoint(area, ColumnsFor(area, m) - 1, maxRow, m);
                desktop.Icons[index].X = last.X;
                desktop.Icons[index].Y = last.Y;
                index++;
            }
        }

        /// <summary>下一个空位（列优先；满了返回区域外坐标）。</summary>
        public static Point NextFreeCell(DesktopProfile desktop, Rect area)
        {
            return NextFreeCell(desktop, area, DefaultMetrics);
        }

        public static Point NextFreeCell(DesktopProfile desktop, Rect area, IconMetrics metrics)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            int maxCol = ColumnsFor(area, m) - 1;
            int maxRow = RowsFor(area, m) - 1;
            for (int col = 0; col <= maxCol; col++)
            {
                for (int row = 0; row <= maxRow; row++)
                {
                    if (!IsOccupied(desktop, null, col, row, area, m))
                    {
                        return CellToPoint(area, col, row, m);
                    }
                }
            }
            Point overflow = CellToPoint(area, maxCol, maxRow, m);
            return new Point(overflow.X + m.CellWidth, overflow.Y);
        }

        /// <summary>指定栅格是否已被其他图标占用。</summary>
        public static bool IsOccupied(DesktopProfile desktop, string exceptIconId, int col, int row, Rect area)
        {
            return IsOccupied(desktop, exceptIconId, col, row, area, DefaultMetrics);
        }

        public static bool IsOccupied(DesktopProfile desktop, string exceptIconId, int col, int row, Rect area, IconMetrics metrics)
        {
            if (desktop == null || desktop.Icons == null) return false;
            IconMetrics m = metrics ?? DefaultMetrics;
            foreach (IconEntry ic in desktop.Icons)
            {
                if (ic == null) continue;
                if (exceptIconId != null && string.Equals(ic.Id, exceptIconId, StringComparison.Ordinal)) continue;
                int c, r;
                PointToCell(area, new Point(ic.X, ic.Y), m, out c, out r);
                if (c == col && r == row) return true;
            }
            return false;
        }

        /// <summary>
        /// 把图标放到期望位置：吸附到栅格；若该格被占用则寻找最近的空格。
        /// 返回吸附后的坐标。
        /// </summary>
        public static Point PlaceIcon(DesktopProfile desktop, IconEntry icon, Rect area, Point desired)
        {
            return PlaceIcon(desktop, icon, area, desired, DefaultMetrics);
        }

        public static Point PlaceIcon(DesktopProfile desktop, IconEntry icon, Rect area, Point desired, IconMetrics metrics)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            int wantCol, wantRow;
            PointToCell(area, desired, m, out wantCol, out wantRow);
            int maxCol = ColumnsFor(area, m) - 1;
            int maxRow = RowsFor(area, m) - 1;

            if (!IsOccupied(desktop, icon == null ? null : icon.Id, wantCol, wantRow, area, m))
            {
                return CellToPoint(area, wantCol, wantRow, m);
            }

            int freeCol, freeRow;
            if (FindNearestFreeCell(desktop, icon == null ? null : icon.Id, area, wantCol, wantRow, out freeCol, out freeRow, m))
            {
                return CellToPoint(area, freeCol, freeRow, m);
            }
            return CellToPoint(area, maxCol + 1, Math.Min(wantRow, maxRow), m);
        }

        /// <summary>找到离 (wantCol,wantRow) 最近的空格（曼哈顿距离，环形向外搜索）。</summary>
        public static bool FindNearestFreeCell(DesktopProfile desktop, string exceptIconId, Rect area,
                                               int wantCol, int wantRow, out int col, out int row)
        {
            return FindNearestFreeCell(desktop, exceptIconId, area, wantCol, wantRow, out col, out row, DefaultMetrics);
        }

        public static bool FindNearestFreeCell(DesktopProfile desktop, string exceptIconId, Rect area,
                                               int wantCol, int wantRow, out int col, out int row, IconMetrics metrics)
        {
            IconMetrics m = metrics ?? DefaultMetrics;
            int maxCol = ColumnsFor(area, m) - 1;
            int maxRow = RowsFor(area, m) - 1;
            int maxRadius = Math.Max(maxCol, maxRow) + 2;
            for (int radius = 0; radius <= maxRadius; radius++)
            {
                for (int dc = -radius; dc <= radius; dc++)
                {
                    for (int dr = -radius; dr <= radius; dr++)
                    {
                        if (Math.Abs(dc) + Math.Abs(dr) != radius) continue;
                        int c = wantCol + dc;
                        int r = wantRow + dr;
                        if (c < 0 || r < 0 || c > maxCol || r > maxRow) continue;
                        if (!IsOccupied(desktop, exceptIconId, c, r, area, m))
                        {
                            col = c;
                            row = r;
                            return true;
                        }
                    }
                }
            }
            col = -1;
            row = -1;
            return false;
        }

        /// <summary>
        /// 规整所有图标位置：越界、重叠、未初始化的图标重新分配空格；有效位置吸附到栅格。
        /// 用于首次导入、分辨率变化、从旧配置读取之后。
        /// </summary>
        public static void Normalize(DesktopProfile desktop, Rect area)
        {
            Normalize(desktop, area, DefaultMetrics);
        }

        public static void Normalize(DesktopProfile desktop, Rect area, IconMetrics metrics)
        {
            if (desktop == null || desktop.Icons == null) return;
            IconMetrics m = metrics ?? DefaultMetrics;
            int maxCol = ColumnsFor(area, m) - 1;
            int maxRow = RowsFor(area, m) - 1;
            HashSet<string> usedCells = new HashSet<string>(StringComparer.Ordinal);

            // 第一遍：保留有效且不冲突的位置
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry ic = desktop.Icons[i];
                if (ic == null) continue;
                int c, r;
                PointToCell(area, new Point(ic.X, ic.Y), m, out c, out r);
                Point snapped = CellToPoint(area, c, r, m);
                bool nearGrid = Math.Abs(snapped.X - ic.X) <= 2.0 && Math.Abs(snapped.Y - ic.Y) <= 2.0;
                bool inRange = c >= 0 && c <= maxCol && r >= 0 && r <= maxRow;
                string key = c + "," + r;
                if (nearGrid && inRange && !usedCells.Contains(key))
                {
                    ic.X = snapped.X;
                    ic.Y = snapped.Y;
                    usedCells.Add(key);
                }
                else
                {
                    ic.X = double.NaN;
                    ic.Y = double.NaN;
                }
            }

            // 第二遍：给待分配图标找空位
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry ic = desktop.Icons[i];
                if (ic == null || !double.IsNaN(ic.X)) continue;
                bool found = false;
                for (int col = 0; col <= maxCol && !found; col++)
                {
                    for (int row = 0; row <= maxRow && !found; row++)
                    {
                        string key = col + "," + row;
                        if (usedCells.Contains(key)) continue;
                        Point p = CellToPoint(area, col, row, m);
                        ic.X = p.X;
                        ic.Y = p.Y;
                        usedCells.Add(key);
                        found = true;
                    }
                }
                if (!found)
                {
                    Point p = CellToPoint(area, maxCol + 1, 0, m);
                    ic.X = p.X;
                    ic.Y = p.Y;
                }
            }
        }

        /// <summary>
        /// 自由摆放模式下的位置修正：只把 NaN / 屏幕外的坐标夹回屏幕范围，不做网格吸附与避让。
        /// </summary>
        public static void NormalizeFree(DesktopProfile desktop, Rect area)
        {
            if (desktop == null || desktop.Icons == null) return;
            double maxX = area.X + area.Width - 48;
            double maxY = area.Y + area.Height - 48;
            int fallback = 0;
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry ic = desktop.Icons[i];
                if (ic == null) continue;
                if (double.IsNaN(ic.X) || double.IsInfinity(ic.X)) ic.X = area.X + MarginX;
                if (double.IsNaN(ic.Y) || double.IsInfinity(ic.Y)) ic.Y = area.Y + MarginY + fallback * 16;
                if (ic.X < area.X) ic.X = area.X;
                if (ic.Y < area.Y) ic.Y = area.Y;
                if (ic.X > maxX) ic.X = maxX;
                if (ic.Y > maxY) ic.Y = maxY;
                fallback++;
            }
        }
    }
}
