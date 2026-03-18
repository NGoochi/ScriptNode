using System;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;

namespace ScriptNodePlugin
{
    public class ScriptNodeInfo : GH_AssemblyInfo
    {
        public override string Name => "ScriptNode";

        public override Bitmap Icon => null;

        public override string Description =>
            "Auto-updating external Python scripts (ScriptNode) and schema-driven data editing (DataNode) for Grasshopper.";

        public override Guid Id => new Guid("E3A7F1B2-4C8D-4E6F-A1B3-9D2E5F7A8C01");

        public override string AuthorName => "Nick Gauci";

        public override string AuthorContact => "";

        public override string AssemblyVersion =>
            GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0";
    }
}
