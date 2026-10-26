using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDesk.Core;

namespace MultiDesk.UI
{
    /// <summary>
    /// 桌面上单个图标的可视元素（1.2.2 起按真实 Windows 桌面度量精修）：
    ///   * 单元格大小 = metrics.CellWidth x metrics.CellHeight（用户机约 76.7 x 98 DIP）；
    ///   * 图标 glyph 在单元格内水平居中（left = (CellWidth-IconSize)/2），垂直顶部内边距 1.5 DIP；
    ///   * 标签宽度 = CellWidth-2（限制在单元格内，不再与邻居文字重合），居中于单元格；
    ///     Segoe UI 12 DIP、行高 16、最多两行省略号、白字 + 深色阴影；
    ///   * 取不到图标时绘制圆角占位块 + 名称首字；
    ///   * 选中/悬停使用 Windows 风格半透明圆角高亮（宽=单元格，覆盖图标+标签区域，圆角 3）；
    ///   * 拖动时半透明并提升 Z 序。
    ///
    /// 该元素自身只负责显示，鼠标交互统一由 DesktopLayerWindow 处理。
    /// </summary>
    public class DesktopLayerIconView : Grid
    {
        /// <summary>Windows 桌面图标标签字号（DIP，约等于 Segoe UI 9pt）。</summary>
        public const double LabelFontSize = 12.0;
        /// <summary>标签行高（DIP）。</summary>
        public const double LabelLineHeight = 16.0;
        /// <summary>图标相对单元格顶部的内边距（DIP，真实桌面约 1.3~1.5）。</summary>
        public const double IconTopPadding = 1.5;

        private static readonly Brush SelectedFill = CreateFrozenBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SelectedBorder = CreateFrozenBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        private static readonly Brush HoverFill = CreateFrozenBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        private static readonly Brush LabelForeground = CreateFrozenBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly Brush LabelShadow = CreateFrozenBrush(Color.FromArgb(0xD0, 0x00, 0x00, 0x00));
        private static readonly FontFamily LabelFont = new FontFamily("Segoe UI");

        private static readonly Color[] PlaceholderColors = new Color[]
        {
            Color.FromRgb(0x3B, 0x82, 0xF6),
            Color.FromRgb(0x8B, 0x5C, 0xF6),
            Color.FromRgb(0xEC, 0x48, 0x99),
            Color.FromRgb(0xF9, 0x73, 0x16),
            Color.FromRgb(0x10, 0xB9, 0x81),
            Color.FromRgb(0x06, 0xB6, 0xD4),
            Color.FromRgb(0x64, 0x74, 0x8B),
            Color.FromRgb(0xDC, 0x26, 0x26)
        };

        private readonly IconMetrics _metrics;
        private readonly Border _highlight;
        private readonly Image _image;
        private readonly Border _placeholder;
        private readonly TextBlock _placeholderText;
        private readonly TextBlock _label;
        private readonly TextBlock _labelShadow;

        private bool _selected;
        private bool _hover;
        private bool _dragging;

        /// <summary>该图标对应的数据对象。</summary>
        public IconEntry Entry { get; private set; }

        /// <summary>当前使用的图标尺寸参数。</summary>
        public IconMetrics Metrics
        {
            get { return _metrics; }
        }

        public DesktopLayerIconView(IconEntry entry, IconMetrics metrics)
        {
            Entry = entry;
            _metrics = metrics != null ? metrics : IconLayout.Default;

            double iconSize = _metrics.IconSize;
            double cellWidth = _metrics.CellWidth;
            double cellHeight = _metrics.CellHeight;
            // 标签宽 = 单元格宽 - 2：限制在格内换行，避免相邻图标文字互相重合
            double labelWidth = Math.Max(24.0, cellWidth - 2.0);
            double labelLeft = (cellWidth - labelWidth) / 2.0;
            // 图标 glyph 在单元格内水平居中（真实桌面左内边距≈14.3 DIP），垂直顶部内边距 1.5 DIP
            double iconLeft = (cellWidth - iconSize) / 2.0;
            if (iconLeft < 0.0) iconLeft = 0.0;
            // 选中高亮：宽=单元格，高≈图标(1.5+iconSize)+标签最多两行
            double highlightHeight = IconTopPadding + iconSize + LabelLineHeight * 2.0 + 1.0;

            Width = cellWidth;
            Height = cellHeight;
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            // ---- 选中 / 悬停高亮（Windows 风格：宽=单元格、覆盖图标+标签区域，位于内容下方）----
            _highlight = new Border();
            _highlight.Width = cellWidth;
            _highlight.Height = highlightHeight;
            _highlight.CornerRadius = new CornerRadius(3.0);
            _highlight.BorderThickness = new Thickness(1.0);
            _highlight.BorderBrush = Brushes.Transparent;
            _highlight.Background = Brushes.Transparent;
            _highlight.HorizontalAlignment = HorizontalAlignment.Left;
            _highlight.VerticalAlignment = VerticalAlignment.Top;
            _highlight.Visibility = Visibility.Collapsed;
            _highlight.IsHitTestVisible = false;
            Children.Add(_highlight);

            // ---- 内容（不参与命中测试，让点击落在单元格本身）----
            Grid content = new Grid();
            content.IsHitTestVisible = false;
            content.RowDefinitions.Add(new RowDefinition());
            content.RowDefinitions[0].Height = new GridLength(iconSize + 2.0);
            content.RowDefinitions.Add(new RowDefinition());
            content.RowDefinitions[1].Height = new GridLength(1.0, GridUnitType.Star);
            Children.Add(content);

            // 图标区：单元格内水平居中 + 顶部 1.5 DIP 内边距
            Grid iconHost = new Grid();
            iconHost.Width = iconSize;
            iconHost.Height = iconSize;
            iconHost.HorizontalAlignment = HorizontalAlignment.Left;
            iconHost.VerticalAlignment = VerticalAlignment.Top;
            iconHost.Margin = new Thickness(iconLeft, IconTopPadding, 0.0, 0.0);
            content.Children.Add(iconHost);

            _placeholder = new Border();
            _placeholder.Width = iconSize;
            _placeholder.Height = iconSize;
            _placeholder.CornerRadius = new CornerRadius(Math.Max(4.0, iconSize / 5.0));
            _placeholder.Background = CreatePlaceholderBrush(string.Empty);
            _placeholder.Visibility = Visibility.Collapsed;
            _placeholderText = new TextBlock();
            _placeholderText.FontFamily = LabelFont;
            _placeholderText.FontSize = Math.Max(12.0, iconSize * 0.45);
            _placeholderText.FontWeight = FontWeights.SemiBold;
            _placeholderText.Foreground = LabelForeground;
            _placeholderText.HorizontalAlignment = HorizontalAlignment.Center;
            _placeholderText.VerticalAlignment = VerticalAlignment.Center;
            _placeholder.Child = _placeholderText;
            iconHost.Children.Add(_placeholder);

            _image = new Image();
            _image.Width = iconSize;
            _image.Height = iconSize;
            _image.Stretch = Stretch.Uniform;
            _image.SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            iconHost.Children.Add(_image);

            // 文字区：紧贴图标下方，宽=单元格宽-2 并水平居中于单元格；
            // 文字在格内换行（最多两行），不会与相邻图标文字重合。
            Grid labelHost = new Grid();
            labelHost.Width = labelWidth;
            labelHost.HorizontalAlignment = HorizontalAlignment.Left;
            labelHost.VerticalAlignment = VerticalAlignment.Top;
            labelHost.Margin = new Thickness(labelLeft, 0.0, 0.0, 0.0);
            Grid.SetRow(labelHost, 1);
            content.Children.Add(labelHost);

            _labelShadow = CreateLabel(true, labelWidth);
            _label = CreateLabel(false, labelWidth);
            labelHost.Children.Add(_labelShadow);
            labelHost.Children.Add(_label);

            MouseEnter += OnMouseEnterSelf;
            MouseLeave += OnMouseLeaveSelf;

            ApplyContent();
        }

        // ================= 状态 =================

        /// <summary>设置选中状态（半透明白色圆角高亮）。</summary>
        public void SetSelected(bool selected)
        {
            if (_selected == selected) return;
            _selected = selected;
            UpdateHighlight();
        }

        /// <summary>设置悬停状态（更淡的高亮）。</summary>
        public void SetHover(bool hover)
        {
            if (_hover == hover) return;
            _hover = hover;
            UpdateHighlight();
        }

        /// <summary>拖动时半透明并提升 Z 序，松手后复位。</summary>
        public void SetDragging(bool dragging)
        {
            if (_dragging == dragging) return;
            _dragging = dragging;
            Opacity = dragging ? 0.72 : 1.0;
            Panel.SetZIndex(this, dragging ? 200 : 10);
        }

        // ================= 内部实现 =================

        private void UpdateHighlight()
        {
            if (_selected)
            {
                _highlight.Visibility = Visibility.Visible;
                _highlight.Background = SelectedFill;
                _highlight.BorderBrush = SelectedBorder;
                _highlight.BorderThickness = new Thickness(1.0);
            }
            else if (_hover)
            {
                _highlight.Visibility = Visibility.Visible;
                _highlight.Background = HoverFill;
                _highlight.BorderBrush = Brushes.Transparent;
                _highlight.BorderThickness = new Thickness(0.0);
            }
            else
            {
                _highlight.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyContent()
        {
            string name = null;
            string target = null;
            if (Entry != null)
            {
                name = Entry.DisplayName;
                target = Entry.TargetPath;
            }
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(target) ? "图标" : target;

            _label.Text = name;
            _labelShadow.Text = name;

            ImageSource source = null;
            try
            {
                // 按图标显示尺寸的 2 倍取图，保证高分屏下清晰
                int pixelSize = (int)Math.Round(_metrics.IconSize * 2.0);
                if (pixelSize < 48) pixelSize = 48;
                source = IconService.GetIconForEntry(Entry, pixelSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopLayerIconView] 提取图标失败: " + ex.Message);
            }

            if (source != null)
            {
                _image.Source = source;
                _image.Visibility = Visibility.Visible;
                _placeholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
                _placeholder.Background = CreatePlaceholderBrush(name);
                _placeholderText.Text = FirstVisibleChar(name);
                _placeholder.Visibility = Visibility.Visible;
            }
        }

        private static TextBlock CreateLabel(bool shadow, double labelWidth)
        {
            TextBlock text = new TextBlock();
            text.FontFamily = LabelFont;
            text.FontSize = LabelFontSize;
            text.TextAlignment = TextAlignment.Center;
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.LineHeight = LabelLineHeight;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            text.MaxHeight = LabelLineHeight * 2.0;   // 最多两行
            text.MaxWidth = Math.Max(24.0, labelWidth);
            text.HorizontalAlignment = HorizontalAlignment.Stretch;
            text.VerticalAlignment = VerticalAlignment.Top;
            if (shadow)
            {
                text.Foreground = LabelShadow;
                text.Margin = new Thickness(0.0, 1.0, 0.0, 0.0);
                text.IsHitTestVisible = false;
            }
            else
            {
                text.Foreground = LabelForeground;
            }
            return text;
        }

        private static string FirstVisibleChar(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            try
            {
                if (text.Length >= 2 && char.IsHighSurrogate(text[0])) return text.Substring(0, 2);
                return text.Substring(0, 1);
            }
            catch
            {
                return "?";
            }
        }

        private static Brush CreatePlaceholderBrush(string name)
        {
            int hash = 0;
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < name.Length; i++)
                {
                    hash = (hash * 31 + name[i]) & 0x7FFFFFFF;
                }
            }
            Color baseColor = PlaceholderColors[hash % PlaceholderColors.Length];
            Color light = Color.FromArgb(
                baseColor.A,
                (byte)Math.Min(255, baseColor.R + 34),
                (byte)Math.Min(255, baseColor.G + 34),
                (byte)Math.Min(255, baseColor.B + 34));
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0.0, 0.0);
            brush.EndPoint = new Point(1.0, 1.0);
            brush.GradientStops.Add(new GradientStop(light, 0.0));
            brush.GradientStops.Add(new GradientStop(baseColor, 1.0));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void OnMouseEnterSelf(object sender, MouseEventArgs e)
        {
            SetHover(true);
        }

        private void OnMouseLeaveSelf(object sender, MouseEventArgs e)
        {
            SetHover(false);
        }
    }
}
