using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Describes a single parsed input from the NODE_INPUTS header.
    /// </summary>
    public readonly struct InputDef : IEquatable<InputDef>
    {
        public string Name { get; }
        public string TypeHint { get; }
        public bool IsList { get; }

        public InputDef(string name, string typeHint, bool isList)
        {
            Name = name;
            TypeHint = typeHint;
            IsList = isList;
        }

        public bool Equals(InputDef other) =>
            Name == other.Name && TypeHint == other.TypeHint && IsList == other.IsList;

        public override bool Equals(object obj) => obj is InputDef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Name, TypeHint, IsList);
    }

    /// <summary>
    /// Parsed header from a .py script file.
    /// </summary>
    public class ScriptHeader : IEquatable<ScriptHeader>
    {
        public List<InputDef> Inputs { get; }
        public List<string> Outputs { get; }

        public ScriptHeader(List<InputDef> inputs, List<string> outputs)
        {
            Inputs = inputs ?? new List<InputDef>();
            Outputs = outputs ?? new List<string>();
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

    /// <summary>
    /// Parses NODE_INPUTS / NODE_OUTPUTS header comments from Python script files.
    /// </summary>
    public static class HeaderParser
    {
        private const int MAX_HEADER_LINES = 200;

        private static readonly Regex InputsRegex =
            new Regex(@"^#\s*NODE_INPUTS\s*:\s*(.+)$", RegexOptions.Compiled);

        private static readonly Regex OutputsRegex =
            new Regex(@"^#\s*NODE_OUTPUTS\s*:\s*(.+)$", RegexOptions.Compiled);

        // Matches list[Type] or List[Type]
        private static readonly Regex ListTypeRegex =
            new Regex(@"^[Ll]ist\[(.+)\]$", RegexOptions.Compiled);

        /// <summary>
        /// Parse the header of a Python script file. Returns null if the file
        /// cannot be read (but never throws).
        /// </summary>
        public static ScriptHeader Parse(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var inputs = new List<InputDef>();
                var outputs = new List<string>();

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

        /// <summary>
        /// Parse the full source string (already loaded) instead of reading from disk.
        /// </summary>
        public static ScriptHeader ParseSource(string source)
        {
            if (string.IsNullOrEmpty(source))
                return new ScriptHeader(new List<InputDef>(), new List<string>());

            var inputs = new List<InputDef>();
            var outputs = new List<string>();

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

        private static List<InputDef> ParseInputs(string raw)
        {
            var result = new List<InputDef>();
            var parts = raw.Split(',');
            foreach (var part in parts)
            {
                var token = part.Trim();
                if (string.IsNullOrEmpty(token)) continue;

                var colonIdx = token.IndexOf(':');
                if (colonIdx < 0)
                {
                    // No type hint — default to generic object
                    result.Add(new InputDef(token.Trim(), "object", false));
                    continue;
                }

                var name = token.Substring(0, colonIdx).Trim();
                var typeStr = token.Substring(colonIdx + 1).Trim();

                bool isList = false;
                var listMatch = ListTypeRegex.Match(typeStr);
                if (listMatch.Success)
                {
                    isList = true;
                    typeStr = listMatch.Groups[1].Value.Trim();
                }

                result.Add(new InputDef(name, typeStr, isList));
            }
            return result;
        }

        private static List<string> ParseOutputs(string raw)
        {
            return raw.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        /// <summary>
        /// Create the appropriate GH parameter for a given type hint string.
        /// </summary>
        public static IGH_Param CreateParamForType(string typeHint, string name, bool isList)
        {
            IGH_Param param;
            switch (typeHint.ToLowerInvariant())
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
            param.Description = $"Dynamic input: {name} ({typeHint})";
            param.Access = isList ? GH_ParamAccess.list : GH_ParamAccess.item;
            param.Optional = true;

            return param;
        }

        /// <summary>
        /// Create a generic output parameter.
        /// </summary>
        public static IGH_Param CreateOutputParam(string name)
        {
            var param = new Param_GenericObject();
            param.Name = name;
            param.NickName = name;
            param.Description = $"Dynamic output: {name}";
            param.Access = GH_ParamAccess.item;

            return param;
        }
    }
}
