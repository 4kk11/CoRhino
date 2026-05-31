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

            // Re-running CoRhino after the claude session ended (e.g. the user typed
            // /exit) must respawn it. Rhino caches the System panel instance, so the
            // constructor — and its one-shot StartSession — does not re-run on reopen;
            // we have to ask the live instance to restart its session explicitly.
            // No-op when the session is still alive (panel just comes to front).
            Panels.GetPanel<CoRhinoPanel>(doc)?.EnsureSessionAlive();

            return Result.Success;
        }
    }
}
