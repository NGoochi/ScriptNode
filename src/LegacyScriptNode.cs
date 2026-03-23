using System;

using Grasshopper.Kernel;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Hidden shim with the original ScriptNode component GUID so old .gh files load.
    /// Functionally identical to <see cref="AlienNodeComponent"/>.
    /// </summary>
    public sealed class LegacyScriptNode : AlienNodeComponent
    {
        public override Guid ComponentGuid =>
            new Guid("A1B2C3D4-E5F6-7890-ABCD-EF0123456789");

        public override GH_Exposure Exposure => GH_Exposure.hidden;
    }
}
