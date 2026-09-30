using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.HDRadio
{
    public class HDRadioPlugin : ISharpPlugin, ICanLazyLoadGui, IMustLoadGui, IPreferedDockPosition, ISupportStatus, IExtendedNameProvider
    {
        private ControlPanel _gui;
        private ISharpControl _control;

        public const string Name = "808 HD";

        public string DisplayName => Name;

        public string Category => "Digital";

        public string MenuItemName => DisplayName;

        // Load at startup so decoding (and the now-playing strip) work without opening the panel.
        public bool LoadNeeded => true;

        // Dock with the radio controls on the left instead of opening as a floating window.
        public DockStyle DockPosition => DockStyle.Left;

        public bool IsActive => _gui != null && _gui.DecoderRunning;

        public UserControl Gui
        {
            get
            {
                LoadGui();
                return _gui;
            }
        }

        public void LoadGui()
        {
            if (_gui == null)
            {
                _gui = new ControlPanel(_control);
            }
        }

        public void Initialize(ISharpControl control)
        {
            _control = control;
            LoadGui();
        }

        public void Close()
        {
            _gui?.Shutdown();
        }
    }
}
