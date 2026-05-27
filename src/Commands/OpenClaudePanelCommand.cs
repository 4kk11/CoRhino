using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoClaudePanel.Panel;

namespace RhinoClaudePanel.Commands
{
    public class OpenClaudePanelCommand : Command
    {
        public override string EnglishName => "RhinoClaude";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(typeof(ClaudePanel).GUID);
            return Result.Success;
        }
    }
}
