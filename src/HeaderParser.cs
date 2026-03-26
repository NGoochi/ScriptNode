using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;

namespace ScriptNodePlugin
{
    /// <summary>Optional per-parameter metadata from header (description, slider hints).</summary>
    public readonly struct ParamMetadata : IEquatable<ParamMetadata>
    {
        public string Description { get; }
        public double? Min { get; }
        public double? Max { get; }
        public double? Step { get; }
        public int? Decimals { get; }
        public double? Default { get; }
        /// <summary>Name of another input whose integer value drives this param's list count.</summary>
        public string DrivenBy { get; }

        public ParamMetadata(string description, double? min, double? max, double? step,
            int? decimals = null, double? defaultVal = null, string drivenBy = null)
        {
            Description = description ?? "";
            Min = min;
            Max = max;
            Step = step;
            Decimals = decimals;
            Default = defaultVal;
            DrivenBy = drivenBy ?? "";
        }

        public static ParamMetadata Empty => new ParamMetadata("", null, null, null);

        public bool Equals(ParamMetadata other) =>
            Description == other.Description && Min == other.Min && Max == other.Max
            && Step == other.Step && Decimals == other.Decimals
            && Default == other.Default && DrivenBy == other.DrivenBy;

        public override bool Equals(object obj) => obj is ParamMetadata pm && Equals(pm);
        public override int GetHashCode() => HashCode.Combine(
            HashCode.Combine(Description, Min, Max, Step),
            HashCode.Combine(Decimals, Default, DrivenBy));
    }

    /// <summary>One parsed input from NODE_INPUTS.</summary>
    public readonly struct InputDef : IEquatable<InputDef>
    {
        public string Name { get; }
        public string TypeHint { get; }
        public bool IsList { get; }
        public ParamMetadata Meta { get; }

        public InputDef(string name, string typeHint, bool isList, ParamMetadata meta)
        {
            Name = name;
            TypeHint = typeHint;
            IsList = isList;
            Meta = meta;
        }

        public bool Equals(InputDef other) =>
            Name == other.Name && TypeHint == other.TypeHint && IsList == other.IsList && Meta.Equals(other.Meta);

        public override bool Equals(object obj) => obj is InputDef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Name, TypeHint, IsList, Meta);
    }

    /// <summary>One parsed output from NODE_OUTPUTS (optional type + metadata).</summary>
    public readonly struct OutputDef : IEquatable<OutputDef>
    {
        public string Name { get; }
        public string TypeHint { get; }
        public ParamMetadata Meta { get; }

        public OutputDef(string name, string typeHint, ParamMetadata meta)
        {
            Name = name;
            TypeHint = typeHint ?? "";
            Meta = meta;
        }

        public bool Equals(OutputDef other) =>
            Name == other.Name && TypeHint == other.TypeHint && Meta.Equals(other.Meta);

        public override bool Equals(object obj) => obj is OutputDef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Name, TypeHint, Meta);
    }

    /// <summary>Parsed header from a .py script file.</summary>
    public class ScriptHeader : IEquatable<ScriptHeader>
    {
        public List<InputDef> Inputs { get; }
        public List<OutputDef> Outputs { get; }

        public ScriptHeader(List<InputDef> inputs, List<OutputDef> outputs)
        {
            Inputs = inputs ?? new List<InputDef>();
            Outputs = outputs ?? new List<OutputDef>();
        }

        public bool Equals(ScriptHeader other)
        {
            if (other is null) return false;
            return Inputs.SequenceEqual(other.Inputs) && Outputs.SequenceEqual(other.Outputs);
        }

        public override bool Equals(object obj) => Equals(obj as ScriptHeader);
        public override int GetHashCode()
        {
            int hash = 17;
            foreach (var i in Inputs) hash = hash * 31 + i.GetHashCode();
            foreach (var o in Outputs) hash = hash * 31 + o.GetHashCode();
            return hash;
        }
    }

    /// <summary>Parses NODE_INPUTS / NODE_OUTPUTS header comments from Python script files.</summary>
    public static class HeaderParser
    {
        private const int MAX_HEADER_LINES = 200;

        private static readonly Regex InputsRegex =
            new Regex(@"^#\s*NODE_INPUTS\s*:\s*(.+)$", RegexOptions.Compiled);

        private static readonly Regex OutputsRegex =
            new Regex(@"^#\s*NODE_OUTPUTS\s*:\s*(.+)$", RegexOptions.Compiled);

        private static readonly Regex ListTypeRegex =
            new Regex(@"^[Ll]ist\[(.+)\]$", RegexOptions.Compiled);

        private static readonly Regex StructuredMinMax =
            new Regex(@"\bmin\s*=\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StructuredMax =
            new Regex(@"\bmax\s*=\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StructuredStep =
            new Regex(@"\bstep\s*=\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Free-text range: "Range: 0.0 – 100" or "Range: 0-100"</summary>
        private static readonly Regex FreetextRange =
            new Regex(@"Range\s*:\s*([-+]?\d*\.?\d+)\s*[-–—]\s*([-+]?\d*\.?\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StructuredDecimals =
            new Regex(@"\bdecimals\s*=\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StructuredDefault =
            new Regex(@"\bdefault\s*=\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StructuredDrivenBy =
            new Regex(@"\bdriven_by\s*=\s*([A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ScriptHeader Parse(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var inputs = new List<InputDef>();
                var outputs = new List<OutputDef>();

                int lineCount = 0;
                foreach (var rawLine in File.ReadLines(filePath))
                {
                    if (++lineCount > MAX_HEADER_LINES) break;

                    var line = rawLine.Trim();

                    var im = InputsRegex.Match(line);
                    if (im.Success)
                    {
                        inputs.AddRange(ParseInputs(im.Groups[1].Value));
                        continue;
                    }

                    var om = OutputsRegex.Match(line);
                    if (om.Success)
                    {
                        outputs.AddRange(ParseOutputs(om.Groups[1].Value));
                        continue;
                    }
                }

                return new ScriptHeader(inputs, outputs);
            }
            catch
            {
                return null;
            }
        }

        public static ScriptHeader ParseSource(string source)
        {
            if (string.IsNullOrEmpty(source))
                return new ScriptHeader(new List<InputDef>(), new List<OutputDef>());

            var inputs = new List<InputDef>();
            var outputs = new List<OutputDef>();

            int lineCount = 0;
            using var reader = new StringReader(source);
            string rawLine;
            while ((rawLine = reader.ReadLine()) != null)
            {
                if (++lineCount > MAX_HEADER_LINES) break;

                var line = rawLine.Trim();

                var im = InputsRegex.Match(line);
                if (im.Success)
                {
                    inputs.AddRange(ParseInputs(im.Groups[1].Value));
                    continue;
                }

                var om = OutputsRegex.Match(line);
                if (om.Success)
                {
                    outputs.AddRange(ParseOutputs(om.Groups[1].Value));
                    continue;
                }
            }

            return new ScriptHeader(inputs, outputs);
        }

        /// <summary>Split on commas not inside double quotes.</summary>
        internal static List<string> SplitTopLevelCommas(string raw)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') { inQuotes = !inQuotes; sb.Append(c); continue; }
                if (c == ',' && !inQuotes)
                {
                    parts.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) parts.Add(sb.ToString().Trim());
            return parts;
        }

        private static List<InputDef> ParseInputs(string raw)
        {
            var result = new List<InputDef>();
            foreach (var part in SplitTopLevelCommas(raw))
            {
                if (string.IsNullOrEmpty(part)) continue;
                var def = ParseOneInput(part);
                if (def.HasValue) result.Add(def.Value);
            }
            return result;
        }

        private static InputDef? ParseOneInput(string token)
        {
            token = token.Trim();
            if (string.IsNullOrEmpty(token)) return null;

            int pipeIdx = FindFirstPipeOutsideQuotes(token);
            string left, metaPart;
            if (pipeIdx >= 0)
            {
                left = token.Substring(0, pipeIdx).Trim();
                metaPart = token.Substring(pipeIdx + 1).Trim();
            }
            else
            {
                left = token;
                metaPart = "";
            }

            var colonIdx = left.IndexOf(':');
            if (colonIdx < 0)
                return new InputDef(left.Trim(), "object", false, ParamMetadata.Empty);

            var name = left.Substring(0, colonIdx).Trim();
            var typeStr = left.Substring(colonIdx + 1).Trim();

            bool isList = false;
            var listMatch = ListTypeRegex.Match(typeStr);
            if (listMatch.Success)
            {
                isList = true;
                typeStr = listMatch.Groups[1].Value.Trim();
            }

            var meta = ParseMetadata(metaPart);
            return new InputDef(name, typeStr, isList, meta);
        }

        private static List<OutputDef> ParseOutputs(string raw)
        {
            var result = new List<OutputDef>();
            foreach (var part in SplitTopLevelCommas(raw))
            {
                if (string.IsNullOrEmpty(part)) continue;
                var def = ParseOneOutput(part);
                if (def.HasValue) result.Add(def.Value);
            }
            return result;
        }

        private static OutputDef? ParseOneOutput(string token)
        {
            token = token.Trim();
            if (string.IsNullOrEmpty(token)) return null;

            int pipeIdx = FindFirstPipeOutsideQuotes(token);
            string left, metaPart;
            if (pipeIdx >= 0)
            {
                left = token.Substring(0, pipeIdx).Trim();
                metaPart = token.Substring(pipeIdx + 1).Trim();
            }
            else
            {
                left = token;
                metaPart = "";
            }

            var colonIdx = left.IndexOf(':');
            string name;
            string typeHint;
            if (colonIdx < 0)
            {
                name = left;
                typeHint = "";
            }
            else
            {
                name = left.Substring(0, colonIdx).Trim();
                typeHint = left.Substring(colonIdx + 1).Trim();
            }

            var meta = ParseMetadata(metaPart);
            return new OutputDef(name, typeHint, meta);
        }

        private static int FindFirstPipeOutsideQuotes(string s)
        {
            bool inQ = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') { inQ = !inQ; continue; }
                if (s[i] == '|' && !inQ) return i;
            }
            return -1;
        }

        internal static ParamMetadata ParseMetadata(string metaPart)
        {
            if (string.IsNullOrWhiteSpace(metaPart))
                return ParamMetadata.Empty;

            string rest = metaPart.Trim();
            string description = "";

            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int i = 1;
                while (i < rest.Length && rest[i] != '"') i++;
                if (i < rest.Length)
                {
                    description = rest.Substring(1, i - 1);
                    rest = rest.Substring(i + 1).Trim();
                }
            }

            double? min = null, max = null, step = null;
            var mm = StructuredMinMax.Match(rest);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vmin))
                min = vmin;
            var xm = StructuredMax.Match(rest);
            if (xm.Success && double.TryParse(xm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vmax))
                max = vmax;
            var sm = StructuredStep.Match(rest);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vst))
                step = vst;

            if (min == null || max == null)
            {
                var fr = FreetextRange.Match(description + " " + rest);
                if (fr.Success
                    && double.TryParse(fr.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    && double.TryParse(fr.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                {
                    min ??= Math.Min(a, b);
                    max ??= Math.Max(a, b);
                }
            }

            int? decimals = null;
            var dm = StructuredDecimals.Match(rest);
            if (dm.Success && int.TryParse(dm.Groups[1].Value, out var vdec))
                decimals = vdec;

            double? defaultVal = null;
            var dfm = StructuredDefault.Match(rest);
            if (dfm.Success && double.TryParse(dfm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vdef))
                defaultVal = vdef;

            string drivenBy = null;
            var dbm = StructuredDrivenBy.Match(rest);
            if (dbm.Success)
                drivenBy = dbm.Groups[1].Value;

            return new ParamMetadata(description, min, max, step, decimals, defaultVal, drivenBy);
        }

        public static IGH_Param CreateParamForType(string typeHint, string name, bool isList, ParamMetadata meta = default)
        {
            IGH_Param param;
            switch ((typeHint ?? "").ToLowerInvariant())
            {
                case "point3d":
                case "point":
                    param = new Param_Point();
                    break;
                case "vector3d":
                case "vector":
                    param = new Param_Vector();
                    break;
                case "plane":
                    param = new Param_Plane();
                    break;
                case "line":
                    param = new Param_Line();
                    break;
                case "curve":
                    param = new Param_Curve();
                    break;
                case "surface":
                    param = new Param_Surface();
                    break;
                case "brep":
                    param = new Param_Brep();
                    break;
                case "mesh":
                    param = new Param_Mesh();
                    break;
                case "int":
                case "integer":
                    param = new Param_Integer();
                    break;
                case "float":
                case "double":
                case "number":
                    param = new Param_Number();
                    break;
                case "str":
                case "string":
                    param = new Param_String();
                    break;
                case "bool":
                case "boolean":
                    param = new Param_Boolean();
                    break;
                case "color":
                case "colour":
                    param = new Param_Colour();
                    break;
                case "geometry":
                    param = new Param_Geometry();
                    break;
                default:
                    param = new Param_GenericObject();
                    break;
            }

            param.Name = name;
            param.NickName = name;
            var desc = string.IsNullOrEmpty(meta.Description)
                ? $"Dynamic input: {name} ({typeHint})"
                : $"{meta.Description}\n({typeHint})";
            param.Description = desc;
            param.Access = isList ? GH_ParamAccess.list : GH_ParamAccess.item;
            param.Optional = true;

            return param;
        }

        public static IGH_Param CreateOutputParam(OutputDef def)
        {
            if (!string.IsNullOrEmpty(def.TypeHint))
                return CreateParamForType(def.TypeHint, def.Name, false, def.Meta);

            var param = new Param_GenericObject();
            param.Name = def.Name;
            param.NickName = def.Name;
            param.Description = string.IsNullOrEmpty(def.Meta.Description)
                ? $"Dynamic output: {def.Name}"
                : def.Meta.Description;
            param.Access = GH_ParamAccess.item;
            return param;
        }

        /// <summary>Backward-compatible: output name only.</summary>
        public static IGH_Param CreateOutputParam(string name)
        {
            return CreateOutputParam(new OutputDef(name, "", ParamMetadata.Empty));
        }
    }
}
