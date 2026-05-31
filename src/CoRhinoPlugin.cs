using System.Runtime.InteropServices;
using Rhino.PlugIns;
using Rhino.UI;
using CoRhino.Panel;

[assembly: Guid("4C13D5E0-9E5A-4F18-9C2A-1E9E1A7B3C01")]

namespace CoRhino
{
    public class CoRhinoPlugin : PlugIn
    {
        public static CoRhinoPlugin Instance { get; private set; } = null!;

        public CoRhinoPlugin()
        {
            Instance = this;
        }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Panels.RegisterPanel(
                this,
                typeof(CoRhinoPanel),
                "CoRhino",
                typeof(CoRhinoPlugin).Assembly,
                "CoRhino.icon.png",
                PanelType.System);

            return LoadReturnCode.Success;
        }
    }
}
