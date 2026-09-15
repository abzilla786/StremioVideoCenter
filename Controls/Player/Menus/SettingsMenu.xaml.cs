using System.Windows.Controls;

namespace StremioVideoCenterControls.Player.Menus
{
    public partial class SettingsMenu : UserControl
    {
        public SettingsMenu()
        {
            InitializeComponent();
        }

        // Returns a FocusGroup containing whatever is focusable in this menu
        // right now. Called fresh each time the menu opens so future settings
        // rows are picked up automatically without changing MpvPlayer.xaml.cs.
        public FocusGroup BuildFocusGroup()
        {
            FocusGroup group = new FocusGroup();
            group.Add(RootBorder);
            return group;
        }
    }
}
