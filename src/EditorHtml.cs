using System;
using System.IO;
using System.Reflection;

namespace ScriptNodePlugin
{
    public static class EditorHtml
    {
        private static string _webRoot;

        private static string WebRoot
        {
            get
            {
                if (_webRoot != null) return _webRoot;
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var candidate = Path.Combine(asmDir, "web");
                if (Directory.Exists(candidate))
                {
                    _webRoot = candidate;
                    return _webRoot;
                }
                var srcDir = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "web"));
                if (Directory.Exists(srcDir))
                {
                    _webRoot = srcDir;
                    return _webRoot;
                }
                _webRoot = candidate;
                return _webRoot;
            }
        }

        public static string GetPage(int port, Guid nodeGuid)
        {
            var path = Path.Combine(WebRoot, "node-editor.html");
            if (!File.Exists(path))
                return FallbackError("node-editor.html", path);

            var html = File.ReadAllText(path);
            html = html.Replace("{{PORT}}", port.ToString());
            html = html.Replace("{{GUID}}", nodeGuid.ToString());
            return html;
        }

        public static string GetDashboard(int port)
        {
            var path = Path.Combine(WebRoot, "dashboard.html");
            if (!File.Exists(path))
                return FallbackError("dashboard.html", path);

            var html = File.ReadAllText(path);
            html = html.Replace("{{PORT}}", port.ToString());
            return html;
        }

        private static string FallbackError(string fileName, string searchedPath)
        {
            return $@"<!DOCTYPE html>
<html><head><title>Alien — Error</title>
<style>body{{background:#111;color:#e0e0e0;font-family:system-ui;padding:40px}}
code{{background:#222;padding:2px 6px;border-radius:4px}}</style></head>
<body><h2>File not found</h2>
<p>Could not find <code>{fileName}</code></p>
<p>Searched: <code>{searchedPath}</code></p>
<p>Make sure the <code>web/</code> folder is next to the <code>.gha</code> assembly or in the source tree.</p>
</body></html>";
        }
    }
}
