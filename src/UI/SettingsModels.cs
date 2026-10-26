using System.Windows;
using System.Windows.Media;

namespace MultiDesk.UI
{
    /// <summary>侧栏桌面列表项。每次刷新重新创建，因此不需要属性变更通知。</summary>
    public class DesktopListItem
    {
        private string _id = string.Empty;
        private string _displayName = string.Empty;
        private string _indexText = string.Empty;
        private string _iconCountText = string.Empty;
        private bool _isCurrent;

        // 注意：XAML 绑定只能读取“属性”，不能用公共字段。

        public string Id
        {
            get { return _id; }
            set { _id = value; }
        }

        public string DisplayName
        {
            get { return _displayName; }
            set { _displayName = value; }
        }

        public string IndexText
        {
            get { return _indexText; }
            set { _indexText = value; }
        }

        public string IconCountText
        {
            get { return _iconCountText; }
            set { _iconCountText = value; }
        }

        public bool IsCurrent
        {
            get { return _isCurrent; }
            set { _isCurrent = value; }
        }

        /// <summary>“当前”徽章的显示状态。</summary>
        public Visibility CurrentBadgeVisibility
        {
            get { return IsCurrent ? Visibility.Visible : Visibility.Collapsed; }
        }
    }

    /// <summary>桌面图标列表项。</summary>
    public class IconListItem
    {
        private string _iconId = string.Empty;
        private string _displayName = string.Empty;
        private string _targetPath = string.Empty;
        private ImageSource _iconSource;

        public string IconId
        {
            get { return _iconId; }
            set { _iconId = value; }
        }

        public string DisplayName
        {
            get { return _displayName; }
            set { _displayName = value; }
        }

        public string TargetPath
        {
            get { return _targetPath; }
            set { _targetPath = value; }
        }

        public ImageSource IconSource
        {
            get { return _iconSource; }
            set { _iconSource = value; }
        }
    }

    /// <summary>快捷键说明项。</summary>
    public class HotkeyItem
    {
        private string _keyText = string.Empty;
        private string _description = string.Empty;

        public string KeyText
        {
            get { return _keyText; }
            set { _keyText = value; }
        }

        public string Description
        {
            get { return _description; }
            set { _description = value; }
        }

        public HotkeyItem()
        {
        }

        public HotkeyItem(string keyText, string description)
        {
            KeyText = keyText;
            Description = description;
        }
    }
}
