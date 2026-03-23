using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using GH_IO.Serialization;

using Rhino.Geometry;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Persists manual parameter values as strings and casts to GH/Python types using header type hints.
    /// </summary>
    public sealed class ManualValueStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> RawValues => _values;

        public void SetRaw(string paramName, string value)
        {
            if (string.IsNullOrEmpty(paramName)) return;
            if (value == null)
                _values.Remove(paramName);
            else
                _values[paramName] = value;
        }

        public bool TryGetRaw(string paramName, out string value) =>
            _values.TryGetValue(paramName, out value);

        public void Remove(string paramName) => _values.Remove(paramName);

        public void Clear() => _values.Clear();

        /// <summary>
        /// Build a Python input value from stored string + type hint.
        /// </summary>
        public object ResolveValue(string paramName, InputDef def)
        {
            if (!_values.TryGetValue(paramName, out var raw) || raw == null)
                return null;

            try
            {
                if (def.IsList)
                    return ParseList(raw, def.TypeHint);
                return ParseScalar(raw, def.TypeHint);
            }
            catch
            {
                return null;
            }
        }

        private static object ParseList(string raw, string typeHint)
        {
            raw = raw.Trim();
            if (raw.StartsWith("[", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(raw);
                var arr = doc.RootElement;
                if (arr.ValueKind != JsonValueKind.Array) return null;
                var list = new List<object>();
                foreach (var el in arr.EnumerateArray())
                    list.Add(JsonElementToObject(el, typeHint));
                return list;
            }

            // Fallback: comma-separated (no nested commas in geometry — use JSON for complex)
            var parts = SplitCsvRespectingQuotes(raw);
            var result = new List<object>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                result.Add(ParseScalar(t, typeHint));
            }
            return result;
        }

        private static List<string> SplitCsvRespectingQuotes(string raw)
        {
            var parts = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQ = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') { inQ = !inQ; continue; }
                if (c == ',' && !inQ)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        private static object JsonElementToObject(JsonElement el, string typeHint)
        {
            var th = (typeHint ?? "").ToLowerInvariant();
            switch (th)
            {
                case "int":
                case "integer":
                    return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : int.Parse(el.GetString() ?? "0");
                case "float":
                case "double":
                case "number":
                    return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : double.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);
                case "bool":
                case "boolean":
                    return el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False
                        ? el.GetBoolean()
                        : bool.Parse(el.GetString() ?? "false");
                default:
                    if (el.ValueKind == JsonValueKind.String) return el.GetString();
                    if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
                    if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 3)
                    {
                        var a = el.EnumerateArray().GetEnumerator();
                        a.MoveNext(); double x = a.Current.GetDouble();
                        a.MoveNext(); double y = a.Current.GetDouble();
                        a.MoveNext(); double z = a.Current.GetDouble();
                        if (th.Contains("vector")) return new Vector3d(x, y, z);
                        return new Point3d(x, y, z);
                    }
                    return el.ToString();
            }
        }

        private static object ParseScalar(string raw, string typeHint)
        {
            var th = (typeHint ?? "").ToLowerInvariant();
            raw = raw.Trim();

            switch (th)
            {
                case "int":
                case "integer":
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                    return 0;
                case "float":
                case "double":
                case "number":
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                    return 0.0;
                case "bool":
                case "boolean":
                    if (bool.TryParse(raw, out var b)) return b;
                    return false;
                case "str":
                case "string":
                    return raw;
                case "point3d":
                case "point":
                    return ParsePoint3d(raw);
                case "vector3d":
                case "vector":
                    return ParseVector3d(raw);
                case "color":
                case "colour":
                    return ParseColorLoose(raw);
                default:
                    return raw;
            }
        }

        private static Point3d ParsePoint3d(string raw)
        {
            var parts = raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return new Point3d(x, y, z);
            return Point3d.Origin;
        }

        private static Vector3d ParseVector3d(string raw)
        {
            var p = ParsePoint3d(raw);
            return new Vector3d(p.X, p.Y, p.Z);
        }

        private static System.Drawing.Color ParseColorLoose(string raw)
        {
            raw = raw.Trim();
            try
            {
                if (raw.StartsWith("#", StringComparison.Ordinal) && raw.Length >= 7)
                {
                    var hex = raw.Substring(1);
                    if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                        return System.Drawing.Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                }
            }
            catch { }
            return System.Drawing.Color.Black;
        }

        /// <summary>Serialize value from GH/browser to storage string.</summary>
        public static string SerializeForStore(object value, string typeHint, bool isList)
        {
            if (value == null) return "";

            if (isList && value is System.Collections.IList list)
            {
                var items = new List<object>();
                foreach (var o in list) items.Add(o);
                return JsonSerializer.Serialize(items);
            }

            var th = (typeHint ?? "").ToLowerInvariant();
            switch (th)
            {
                case "point3d":
                case "point":
                    if (value is Point3d pt)
                        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", pt.X, pt.Y, pt.Z);
                    break;
                case "vector3d":
                case "vector":
                    if (value is Vector3d v)
                        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", v.X, v.Y, v.Z);
                    break;
            }

            return value.ToString();
        }

        public void Write(GH_IWriter writer)
        {
            writer.SetInt32("ManualValueCount", _values.Count);
            int i = 0;
            foreach (var kv in _values)
            {
                writer.SetString($"Manual_{i}_Name", kv.Key);
                writer.SetString($"Manual_{i}_Value", kv.Value ?? "");
                i++;
            }
        }

        public void Read(GH_IReader reader)
        {
            _values.Clear();
            if (!reader.ItemExists("ManualValueCount")) return;
            int n = reader.GetInt32("ManualValueCount");
            for (int i = 0; i < n; i++)
            {
                var name = reader.GetString($"Manual_{i}_Name");
                var val = reader.ItemExists($"Manual_{i}_Value") ? reader.GetString($"Manual_{i}_Value") : "";
                if (!string.IsNullOrEmpty(name))
                    _values[name] = val ?? "";
            }
        }
    }
}
