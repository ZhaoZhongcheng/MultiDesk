using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MultiDesk.UI
{
    /// <summary>
    /// 桌面图层内部的「自绘右键菜单」（1.2.0 起支持多级子菜单）。
    ///
    /// 不能使用 WPF ContextMenu：桌面图层窗口带 WS_EX_NOACTIVATE（永不激活），
    /// 而 WPF 的 Popup / ContextMenu 依赖窗口激活与焦点，无法可靠弹出。
    /// 因此这里用同一窗口内的全屏透明 Canvas + Border/StackPanel 模拟菜单：
    ///   * 深色圆角面板（#F2181E2B、#33FFFFFF 边框、阴影）；
    ///   * 菜单项 hover 高亮，点击后执行回调并关闭整个菜单；
    ///   * 支持 ▸ 子菜单（悬停或点击展开，弹在父面板右侧，超出屏幕时翻到左侧）；
    ///   * 点击菜单以外的任意位置先关闭菜单。
    /// </summary>
    public class DesktopLayerMenu
    {
        private static readonly Brush PanelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x1E, 0x2B)));
        private static readonly Brush PanelBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush HoverBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush ItemForeground = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0xE9, 0xEE, 0xF6)));
        private static readonly Brush ItemDisabledForeground = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush HeaderForeground = Freeze(new SolidColorBrush(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x6F, 0xA8, 0xFF)));
        private static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI");

        /// <summary>一个菜单面板（根菜单或子菜单）。</summary>
        private sealed class MenuPanel
        {
            public Border Panel;
            public ScrollViewer Scroll;
            public StackPanel Items;
            public MenuPanel Parent;      // 所属上级面板（根为 null）
            public Border ParentItem;     // 在上级面板中触发本子菜单的菜单项（根为 null）
        }

        private readonly Canvas _hostLayer;
        private readonly List<MenuPanel> _panels = new List<MenuPanel>();
        private MenuPanel _root;
        private MenuPanel _current;
        private bool _isOpen;

        /// <summary>菜单是否处于打开状态。</summary>
        public bool IsOpen
        {
            get { return _isOpen; }
        }

        /// <summary>菜单关闭时触发。</summary>
        public event EventHandler Closed;

        public DesktopLayerMenu(Canvas hostLayer)
        {
            _hostLayer = hostLayer;
            _root = CreatePanel(null, null);
            _panels.Add(_root);
            _current = _root;

            if (_hostLayer != null)
            {
                _hostLayer.MouseLeftButtonDown += OnHostLayerMouseDown;
                _hostLayer.MouseRightButtonDown += OnHostLayerMouseDown;
            }
        }

        // ================= 构建 =================

        /// <summary>开始构建新菜单（清空旧项，回到根面板）。</summary>
        public void Begin()
        {
            try
            {
                DetachAllPanels();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayerMenu] 清理旧菜单失败: " + ex.Message);
            }
            _panels.Clear();
            _root = CreatePanel(null, null);
            _panels.Add(_root);
            _current = _root;
            _isOpen = false;
        }

        /// <summary>添加一个可点击的菜单项。</summary>
        public void AddItem(string text, Action onClick)
        {
            AddItem(text, onClick, false, true);
        }

        /// <summary>添加一个菜单项；isChecked 控制左侧圆点，isEnabled=false 时灰显不可点。</summary>
        public void AddItem(string text, Action onClick, bool isChecked, bool isEnabled)
        {
            MenuPanel parent = _current != null ? _current : _root;
            if (parent == null) return;
            parent.Items.Children.Add(BuildItem(text, onClick, isChecked, isEnabled, null));
        }

        /// <summary>
        /// 添加一个带 ▸ 的子菜单项。buildChildren 内部继续调用 AddItem/AddHeader/AddSeparator
        /// 时，项会加到该子菜单面板里。
        /// </summary>
        public void AddSubmenu(string text, Action<DesktopLayerMenu> buildChildren)
        {
            MenuPanel parent = _current != null ? _current : _root;
            if (parent == null) return;

            MenuPanel child = CreatePanel(parent, null);
            Border item = BuildItem(text, null, false, true, child);
            child.ParentItem = item;
            parent.Items.Children.Add(item);
            _panels.Add(child);

            // 构建子菜单内容
            MenuPanel previous = _current;
            _current = child;
            try
            {
                if (buildChildren != null) buildChildren(this);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayerMenu] 构建子菜单失败: " + ex.Message);
            }
            finally
            {
                _current = previous;
            }
        }

        /// <summary>添加分组标题（不可点击）。</summary>
        public void AddHeader(string text)
        {
            MenuPanel parent = _current != null ? _current : _root;
            if (parent == null) return;
            TextBlock header = new TextBlock();
            header.Text = text == null ? string.Empty : text;
            header.FontSize = 11.0;
            header.FontFamily = UiFont;
            header.Foreground = HeaderForeground;
            header.Margin = new Thickness(10.0, 8.0, 10.0, 3.0);
            parent.Items.Children.Add(header);
        }

        /// <summary>添加分隔线。</summary>
        public void AddSeparator()
        {
            MenuPanel parent = _current != null ? _current : _root;
            if (parent == null) return;
            Border line = new Border();
            line.Height = 1.0;
            line.Margin = new Thickness(8.0, 5.0, 8.0, 5.0);
            line.Background = SeparatorBrush;
            parent.Items.Children.Add(line);
        }

        // ================= 显示 / 关闭 =================

        /// <summary>在窗口坐标 position 处弹出根菜单（自动夹在屏幕内，不超出边界）。</summary>
        public void ShowAt(Point position)
        {
            if (_hostLayer == null || _root == null) return;
            if (_root.Items.Children.Count == 0) return;
            try
            {
                _hostLayer.Visibility = Visibility.Visible;
                if (!_hostLayer.Children.Contains(_root.Panel)) _hostLayer.Children.Add(_root.Panel);
                _root.Panel.Visibility = Visibility.Visible;

                Size size = PreparePanel(_root);
                double screenWidth = ScreenWidth();
                double screenHeight = ScreenHeight();
                double maxLeft = Math.Max(6.0, screenWidth - size.Width - 8.0);
                double maxTop = Math.Max(6.0, screenHeight - size.Height - 8.0);
                double left = position.X;
                double top = position.Y;
                if (left < 6.0) left = 6.0;
                if (left > maxLeft) left = maxLeft;
                if (top < 6.0) top = 6.0;
                if (top > maxTop) top = maxTop;
                Canvas.SetLeft(_root.Panel, left);
                Canvas.SetTop(_root.Panel, top);

                FadeIn(_root.Panel);
                _isOpen = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayerMenu] 显示菜单失败: " + ex);
                Close();
            }
        }

        /// <summary>关闭菜单（幂等）。</summary>
        public void Close()
        {
            bool wasOpen = _isOpen;
            _isOpen = false;
            try
            {
                DetachAllPanels();
                if (_hostLayer != null) _hostLayer.Visibility = Visibility.Collapsed;
                foreach (MenuPanel panel in _panels)
                {
                    if (panel != null && panel.Items != null) panel.Items.Children.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayerMenu] 关闭菜单失败: " + ex.Message);
            }
            if (wasOpen && Closed != null)
            {
                try
                {
                    Closed(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayerMenu] Closed 事件处理失败: " + ex.Message);
                }
            }
        }

        // ================= 内部：面板 / 菜单项 =================

        private MenuPanel CreatePanel(MenuPanel parent, Border parentItem)
        {
            StackPanel items = new StackPanel();
            items.Margin = new Thickness(0.0);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.CanContentScroll = false;
            scroll.Content = items;

            Border panel = new Border();
            panel.Background = PanelBackground;
            panel.BorderBrush = PanelBorder;
            panel.BorderThickness = new Thickness(1.0);
            panel.CornerRadius = new CornerRadius(10.0);
            panel.Padding = new Thickness(6.0);
            panel.MinWidth = 208.0;
            panel.Child = scroll;
            panel.UseLayoutRounding = true;
            panel.Visibility = Visibility.Collapsed;

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 24.0;
            shadow.ShadowDepth = 6.0;
            shadow.Opacity = 0.55;
            shadow.Direction = 270.0;
            panel.Effect = shadow;

            MenuPanel menuPanel = new MenuPanel();
            menuPanel.Panel = panel;
            menuPanel.Scroll = scroll;
            menuPanel.Items = items;
            menuPanel.Parent = parent;
            menuPanel.ParentItem = parentItem;
            return menuPanel;
        }

        /// <summary>构建一个菜单项；submenu 非空时带 ▸ 并在悬停/点击时展开子菜单。</summary>
        private Border BuildItem(string text, Action onClick, bool isChecked, bool isEnabled, MenuPanel submenu)
        {
            string label = text == null ? string.Empty : text;

            Grid layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions[0].Width = new GridLength(16.0);
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions[1].Width = new GridLength(1.0, GridUnitType.Star);
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions[2].Width = new GridLength(14.0);

            TextBlock marker = new TextBlock();
            marker.Text = isChecked ? "●" : string.Empty;
            marker.FontSize = 9.0;
            marker.FontFamily = UiFont;
            marker.Foreground = AccentBrush;
            marker.VerticalAlignment = VerticalAlignment.Center;
            layout.Children.Add(marker);

            TextBlock textBlock = new TextBlock();
            textBlock.Text = label;
            textBlock.FontSize = 13.0;
            textBlock.FontFamily = UiFont;
            textBlock.Foreground = (isEnabled || submenu != null) ? ItemForeground : ItemDisabledForeground;
            textBlock.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(textBlock, 1);
            layout.Children.Add(textBlock);

            if (submenu != null)
            {
                TextBlock arrow = new TextBlock();
                arrow.Text = "▸";
                arrow.FontSize = 11.0;
                arrow.FontFamily = UiFont;
                arrow.Foreground = ItemForeground;
                arrow.VerticalAlignment = VerticalAlignment.Center;
                arrow.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(arrow, 2);
                layout.Children.Add(arrow);
            }

            Border item = new Border();
            item.CornerRadius = new CornerRadius(6.0);
            item.Padding = new Thickness(8.0, 7.0, 10.0, 7.0);
            item.Margin = new Thickness(0.0, 1.0, 0.0, 1.0);
            item.Background = Brushes.Transparent;
            item.Child = layout;
            item.UseLayoutRounding = true;

            if (submenu != null)
            {
                MenuPanel child = submenu;
                item.Cursor = Cursors.Hand;
                item.MouseEnter += delegate
                {
                    item.Background = HoverBackground;
                    OpenSubmenu(child);
                };
                item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
                item.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    OpenSubmenu(child);
                };
            }
            else if (isEnabled)
            {
                item.Cursor = Cursors.Hand;
                item.MouseEnter += delegate { item.Background = HoverBackground; };
                item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
                Action action = onClick;
                item.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    try
                    {
                        Close();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[DesktopLayerMenu] 关闭菜单失败: " + ex.Message);
                    }
                    if (action != null)
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("[DesktopLayerMenu] 菜单命令执行失败: " + ex);
                        }
                    }
                };
            }
            else
            {
                item.Cursor = Cursors.Arrow;
            }

            return item;
        }

        /// <summary>展开子菜单：定位到父菜单项右侧（空间不足时翻到左侧），并显示。</summary>
        private void OpenSubmenu(MenuPanel child)
        {
            if (child == null || child.Parent == null || _hostLayer == null) return;
            try
            {
                if (child.Items.Children.Count == 0) return;
                CloseDescendants(child.Parent);
                if (child.Panel.Visibility == Visibility.Visible) return;

                if (!_hostLayer.Children.Contains(child.Panel)) _hostLayer.Children.Add(child.Panel);
                child.Panel.Visibility = Visibility.Visible;
                Size size = PreparePanel(child);
                _hostLayer.UpdateLayout();

                double parentLeft = Canvas.GetLeft(child.Parent.Panel);
                double parentTop = Canvas.GetTop(child.Parent.Panel);
                if (double.IsNaN(parentLeft)) parentLeft = 0.0;
                if (double.IsNaN(parentTop)) parentTop = 0.0;

                double itemTop = parentTop;
                try
                {
                    if (child.ParentItem != null)
                    {
                        Point origin = child.ParentItem.TranslatePoint(new Point(0.0, 0.0), _hostLayer);
                        itemTop = origin.Y;
                    }
                }
                catch
                {
                }

                double screenWidth = ScreenWidth();
                double screenHeight = ScreenHeight();
                double left = parentLeft + Math.Max(0.0, child.Parent.Panel.ActualWidth) - 2.0;
                if (left + size.Width > screenWidth - 8.0)
                {
                    left = parentLeft - size.Width + 2.0;
                }
                if (left < 6.0) left = 6.0;
                double top = itemTop - 6.0;
                double maxTop = Math.Max(6.0, screenHeight - size.Height - 8.0);
                if (top < 6.0) top = 6.0;
                if (top > maxTop) top = maxTop;

                Canvas.SetLeft(child.Panel, left);
                Canvas.SetTop(child.Panel, top);
                child.Panel.Visibility = Visibility.Visible;
                FadeIn(child.Panel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayerMenu] 展开子菜单失败: " + ex.Message);
            }
        }

        /// <summary>关闭某个面板的所有后代子菜单（用于同一层只展开一个）。</summary>
        private void CloseDescendants(MenuPanel parent)
        {
            if (parent == null || _panels == null) return;
            foreach (MenuPanel panel in _panels)
            {
                if (panel == null || panel == parent) continue;
                if (!IsDescendantOf(panel, parent)) continue;
                HidePanel(panel);
            }
        }

        private static bool IsDescendantOf(MenuPanel panel, MenuPanel ancestor)
        {
            MenuPanel current = panel.Parent;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.Parent;
            }
            return false;
        }

        private void HidePanel(MenuPanel panel)
        {
            if (panel == null) return;
            try
            {
                foreach (MenuPanel child in _panels)
                {
                    if (child != null && child.Parent == panel) HidePanel(child);
                }
                panel.Panel.Visibility = Visibility.Collapsed;
                if (_hostLayer != null && _hostLayer.Children.Contains(panel.Panel))
                {
                    _hostLayer.Children.Remove(panel.Panel);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayerMenu] 隐藏子菜单失败: " + ex.Message);
            }
        }

        private void DetachAllPanels()
        {
            if (_hostLayer == null) return;
            foreach (MenuPanel panel in _panels)
            {
                if (panel == null || panel.Panel == null) continue;
                panel.Panel.BeginAnimation(UIElement.OpacityProperty, null);
                panel.Panel.Opacity = 1.0;
                if (_hostLayer.Children.Contains(panel.Panel)) _hostLayer.Children.Remove(panel.Panel);
                panel.Panel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>测量面板并设置最大高度，返回期望尺寸（DIP）。</summary>
        private static Size PreparePanel(MenuPanel panel)
        {
            double screenHeight = ScreenHeight();
            double maxHeight = Math.Max(140.0, screenHeight - 24.0);
            panel.Panel.MaxHeight = maxHeight;
            panel.Scroll.MaxHeight = Math.Max(100.0, maxHeight - 14.0);
            panel.Panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = panel.Panel.DesiredSize;
            if (size.Width <= 0.0 || size.Height <= 0.0) size = new Size(220.0, 160.0);
            return size;
        }

        private static void FadeIn(Border panel)
        {
            panel.BeginAnimation(UIElement.OpacityProperty, null);
            panel.Opacity = 0.0;
            DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(110.0));
            fade.EasingFunction = new QuadraticEase();
            panel.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static double ScreenWidth()
        {
            double width = SystemParameters.VirtualScreenWidth;
            return width < 200.0 ? 1920.0 : width;
        }

        private static double ScreenHeight()
        {
            double height = SystemParameters.VirtualScreenHeight;
            return height < 200.0 ? 1080.0 : height;
        }

        // ================= 外部点击关闭 =================

        private void OnHostLayerMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isOpen) return;
            e.Handled = true;
            Close();
        }

        private static Brush Freeze(Brush brush)
        {
            try
            {
                if (brush != null && brush.CanFreeze) brush.Freeze();
            }
            catch
            {
            }
            return brush;
        }
    }
}
