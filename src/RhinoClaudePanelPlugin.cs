using System.Runtime.InteropServices;
using Rhino.PlugIns;
using Rhino.UI;
using RhinoClaudePanel.Panel;

[assembly: Guid("4C13D5E0-9E5A-4F18-9C2A-1E9E1A7B3C01")]

namespace RhinoClaudePanel
{
    public class RhinoClaudePanelPlugin : PlugIn
    {
        public static RhinoClaudePanelPlugin Instance { get; private set; } = null!;

        public RhinoClaudePanelPlugin()
        {
            Instance = this;
        }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Panels.RegisterPanel(
                this,
                typeof(ClaudePanel),
                "Claude",
                typeof(RhinoClaudePanelPlugin).Assembly,
                "RhinoClaudePanel.icon.png",
                PanelType.System);

            return LoadReturnCode.Success;
        }
    }
}
