using Rhino;
using Rhino.Commands;
using Rhino.UI;
using CoRhino.Panel;

namespace CoRhino.Commands
{
    public class OpenCoRhinoPanelCommand : Command
    {
        public override string EnglishName => "CoRhino";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(typeof(CoRhinoPanel).GUID);
            return Result.Success;
        }
    }
}
