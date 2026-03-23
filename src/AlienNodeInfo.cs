using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace ScriptNodePlugin
{
    public class AlienNodeInfo : GH_AssemblyInfo
    {
        public override string Name => "Alien";

        public override Bitmap Icon => null;

        public override string Description =>
            "Alien — external Python scripts with dynamic pins, manual values, and browser editor (MCP tools included).";

        public override Guid Id => new Guid("E3A7F1B2-4C8D-4E6F-A1B3-9D2E5F7A8C01");

        public override string AuthorName => "Nick Gauci / AIAA Studio";

        public override string AuthorContact => "";

        public override string Version => "0.2.0";
    }
}
