using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Rhino.Runtime;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Result of executing a Python script.
    /// </summary>
    public class ScriptResult
    {
        /// <summary>Output variable values keyed by name.</summary>
        public Dictionary<string, object> Outputs { get; set; } = new Dictionary<string, object>();

        /// <summary>True if the script executed without error.</summary>
        public bool Success { get; set; }

        /// <summary>Error message if execution failed.</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Full traceback if execution failed.</summary>
        public string Traceback { get; set; }

        /// <summary>Any print() output from the script.</summary>
        public string StdOut { get; set; }
    }

    /// <summary>
    /// Executes Python 3 scripts via Rhino's PythonScript API.
    /// Injects named inputs and collects named outputs.
    /// </summary>
    public static class PythonExecutor
    {
        /// <summary>
        /// Execute a Python source string with named inputs, and collect named outputs.
        /// </summary>
        /// <param name="source">Full Python source code.</param>
        /// <param name="inputs">Dictionary of input variable names to values.</param>
        /// <param name="outputNames">Names of output variables to collect after execution.</param>
        /// <param name="scriptPath">Path to the .py file (for error logging).</param>
        public static ScriptResult Execute(
            string source,
            Dictionary<string, object> inputs,
            IEnumerable<string> outputNames,
            string scriptPath)
        {
            var result = new ScriptResult();

            try
            {
                // Create a Rhino PythonScript instance
                var py = PythonScript.Create();
                if (py == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed to create Python script engine. Is Rhino's Python 3 runtime available?";
                    LogError(scriptPath, result.ErrorMessage, null);
                    return result;
                }

                // Set up output capture
                var stdout = new StringBuilder();
                py.Output += (message) => stdout.AppendLine(message);

                // Inject input variables
                if (inputs != null)
                {
                    foreach (var kvp in inputs)
                    {
                        py.SetVariable(kvp.Key, kvp.Value);
                    }
                }

                // Execute the script
                bool success = py.ExecuteScript(source);
                result.StdOut = stdout.ToString();

                if (!success)
                {
                    result.Success = false;
                    result.ErrorMessage = "Python script execution failed.";
                    // The output may contain the error traceback
                    if (stdout.Length > 0)
                    {
                        result.Traceback = stdout.ToString();
                        result.ErrorMessage = ExtractLastLine(result.Traceback);
                    }
                    LogError(scriptPath, result.ErrorMessage, result.Traceback);
                    return result;
                }

                // Collect output variables
                foreach (var name in outputNames)
                {
                    try
                    {
                        var val = py.GetVariable(name);
                        result.Outputs[name] = val;
                    }
                    catch
                    {
                        // Variable not set by script — leave as null
                        result.Outputs[name] = null;
                    }
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Traceback = ex.ToString();
                LogError(scriptPath, ex.Message, ex.ToString());
            }

            return result;
        }

        /// <summary>
        /// Log an error to gh_errors.log next to the script file.
        /// </summary>
        private static void LogError(string scriptPath, string message, string traceback)
        {
            if (string.IsNullOrEmpty(scriptPath)) return;

            try
            {
                var dir = Path.GetDirectoryName(scriptPath);
                if (string.IsNullOrEmpty(dir)) return;

                var logPath = Path.Combine(dir, "gh_errors.log");
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Path.GetFileName(scriptPath)}\n" +
                            $"  Error: {message}\n";
                if (!string.IsNullOrEmpty(traceback))
                    entry += $"  Traceback:\n    {traceback.Replace("\n", "\n    ")}\n";
                entry += "\n";

                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // Logging should never crash the component
            }
        }

        private static string ExtractLastLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line)) return line;
            }
            return text;
        }
    }
}
