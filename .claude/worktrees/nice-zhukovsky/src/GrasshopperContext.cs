using System;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Thread-safe access to Grasshopper documents.
    /// All GH operations must run on the UI thread; this helper
    /// marshals calls and uses a semaphore to prevent concurrent
    /// document modifications from overlapping HTTP requests.
    /// </summary>
    public class GrasshopperContext
    {
        private static readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Run an action on the Rhino UI thread and return the result.
        /// Blocks the calling (HTTP) thread until the UI thread completes.
        /// </summary>
        public T ExecuteOnUiThread<T>(Func<T> action)
        {
            _mutex.Wait();
            try
            {
                T result = default;
                Exception caught = null;

                RhinoApp.InvokeAndWait(() =>
                {
                    try { result = action(); }
                    catch (Exception ex) { caught = ex; }
                });

                if (caught != null) throw caught;
                return result;
            }
            finally { _mutex.Release(); }
        }

        /// <summary>
        /// Fire-and-forget void variant.
        /// </summary>
        public void ExecuteOnUiThread(Action action)
        {
            _mutex.Wait();
            try
            {
                Exception caught = null;

                RhinoApp.InvokeAndWait(() =>
                {
                    try { action(); }
                    catch (Exception ex) { caught = ex; }
                });

                if (caught != null) throw caught;
            }
            finally { _mutex.Release(); }
        }

        /// <summary>
        /// Get the active Grasshopper document (must be called from UI thread).
        /// </summary>
        public GH_Document GetActiveDocument()
        {
            return Instances.ActiveCanvas?.Document;
        }
    }
}
