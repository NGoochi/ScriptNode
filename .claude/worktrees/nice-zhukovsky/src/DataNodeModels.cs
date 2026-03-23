using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ScriptNodePlugin
{
    // ── Preset ranges for quick slider setup ─────────────────────────
    /// <summary>
    /// Built-in slider presets. Users can always override min/max/decimals.
    /// </summary>
    public static class SliderPresets
    {
        public static readonly (string Name, double Min, double Max, int Decimals)[] All = new[]
        {
            ("normalized",  0.0,    1.0,   3),
            ("percentage",  0.0,  100.0,   2),
            ("angle",       0.0,  360.0,   2),
            ("signed",     -1.0,    1.0,   3),
            ("length_m",    0.0,   50.0,   3),
            ("length_mm",   0.0, 5000.0,   1),
        };

        /// <summary>Apply a named preset to a FieldDef. Returns false if not found.</summary>
        public static bool Apply(FieldDef field, string presetName)
        {
            var match = All.FirstOrDefault(p =>
                string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
            if (match.Name == null) return false;

            field.Min = match.Min;
            field.Max = match.Max;
            field.DecimalPlaces = match.Decimals;
            field.Preset = match.Name;
            return true;
        }
    }

    // ── FieldDef ─────────────────────────────────────────────────────
    /// <summary>
    /// Defines one field in the DataNode schema.
    /// E.g. "floor_area" (float, range 0–500, 2 decimal places).
    /// </summary>
    public class FieldDef : IEquatable<FieldDef>
    {
        /// <summary>Field name, used as output param name and dictionary key.</summary>
        [JsonProperty("name")]
        public string Name { get; set; } = "param";

        /// <summary>
        /// Type hint — reuses the same strings as HeaderParser
        /// (float, int, str, bool, Point3d, etc.).
        /// </summary>
        [JsonProperty("type")]
        public string TypeHint { get; set; } = "float";

        /// <summary>Slider minimum value.</summary>
        [JsonProperty("min")]
        public double Min { get; set; } = 0.0;

        /// <summary>Slider maximum value.</summary>
        [JsonProperty("max")]
        public double Max { get; set; } = 100.0;

        /// <summary>Slider precision (0–5 decimal places).</summary>
        [JsonProperty("decimals")]
        public int DecimalPlaces { get; set; } = 2;

        /// <summary>Preset name (e.g. "angle", "normalized"), or "custom".</summary>
        [JsonProperty("preset")]
        public string Preset { get; set; } = "custom";

        /// <summary>
        /// If true, this field applies globally — a wire override replaces ALL items.
        /// If false, each item has its own value and can be individually overridden.
        /// </summary>
        [JsonProperty("isParent")]
        public bool IsParent { get; set; } = false;

        public FieldDef Clone()
        {
            return new FieldDef
            {
                Name = Name,
                TypeHint = TypeHint,
                Min = Min,
                Max = Max,
                DecimalPlaces = DecimalPlaces,
                Preset = Preset,
                IsParent = IsParent,
            };
        }

        public bool Equals(FieldDef other)
        {
            if (other is null) return false;
            return Name == other.Name
                && TypeHint == other.TypeHint
                && Min == other.Min
                && Max == other.Max
                && DecimalPlaces == other.DecimalPlaces
                && IsParent == other.IsParent;
        }

        public override bool Equals(object obj) => Equals(obj as FieldDef);
        public override int GetHashCode() => HashCode.Combine(Name, TypeHint, Min, Max, DecimalPlaces, IsParent);
    }

    // ── OverrideInfo ─────────────────────────────────────────────────
    /// <summary>
    /// Tracks which fields have their wire-override toggle enabled.
    /// Key format: "fieldName" for parent overrides,
    ///             "itemIndex:fieldName" for per-item overrides.
    /// </summary>
    public class OverrideKey : IEquatable<OverrideKey>
    {
        [JsonProperty("field")]
        public string FieldName { get; set; }

        /// <summary>-1 for parent/global override, 0..N for per-item override.</summary>
        [JsonProperty("item")]
        public int ItemIndex { get; set; } = -1;

        public bool IsParent => ItemIndex < 0;

        /// <summary>
        /// Generate the GH input parameter name for this override.
        /// Parent: "floor_area"
        /// Per-item: "Level 3 floor_area"
        /// </summary>
        public string ToParamName(DataNodeSchema schema)
        {
            if (IsParent) return FieldName;
            if (ItemIndex >= 0 && ItemIndex < schema.Items.Count)
                return $"{schema.Items[ItemIndex].Name} {FieldName}";
            return $"[{ItemIndex}] {FieldName}";
        }

        public bool Equals(OverrideKey other)
        {
            if (other is null) return false;
            return FieldName == other.FieldName && ItemIndex == other.ItemIndex;
        }

        public override bool Equals(object obj) => Equals(obj as OverrideKey);
        public override int GetHashCode() => HashCode.Combine(FieldName, ItemIndex);
    }

    // ── ItemRecord ───────────────────────────────────────────────────
    /// <summary>
    /// One data item (e.g. "Level 14"). Stores a value for each field in the schema.
    /// Value types match the FieldDef.TypeHint — for numeric types, stored as double.
    /// </summary>
    public class ItemRecord
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "Item";

        /// <summary>Field name → value. Numeric values stored as double.</summary>
        [JsonProperty("values")]
        public Dictionary<string, object> Values { get; set; } = new Dictionary<string, object>();

        /// <summary>Get a numeric value, falling back to the field default (midpoint of range).</summary>
        public double GetNumericValue(FieldDef field)
        {
            if (Values.TryGetValue(field.Name, out var val))
            {
                if (val is double d) return d;
                if (val is long l) return l;
                if (val is int i) return i;
                if (double.TryParse(val?.ToString(), out double parsed)) return parsed;
            }
            // Default: midpoint of range
            return (field.Min + field.Max) / 2.0;
        }

        /// <summary>Get a string value with fallback.</summary>
        public string GetStringValue(FieldDef field)
        {
            if (Values.TryGetValue(field.Name, out var val) && val != null)
                return val.ToString();
            return "";
        }

        /// <summary>Set a value, clamping numeric values to the field range.</summary>
        public void SetValue(FieldDef field, object value)
        {
            if (IsNumericType(field.TypeHint) && value is double d)
            {
                d = Math.Max(field.Min, Math.Min(field.Max, d));
                d = Math.Round(d, field.DecimalPlaces);
                Values[field.Name] = d;
            }
            else
            {
                Values[field.Name] = value;
            }
        }

        public ItemRecord Clone()
        {
            return new ItemRecord
            {
                Name = Name,
                Values = new Dictionary<string, object>(Values),
            };
        }

        private static bool IsNumericType(string typeHint)
        {
            switch (typeHint?.ToLowerInvariant())
            {
                case "float":
                case "double":
                case "number":
                case "int":
                case "integer":
                    return true;
                default:
                    return false;
            }
        }
    }

    // ── DataNodeSchema ───────────────────────────────────────────────
    /// <summary>
    /// Full schema + data for one DataNode component.
    /// Contains the field definitions, all items, and override toggle state.
    /// Serializes to/from JSON for .gh persistence and export/import.
    /// </summary>
    public class DataNodeSchema
    {
        /// <summary>Custom display name for the DataNode (shown on the canvas header).</summary>
        [JsonProperty("listName")]
        public string ListName { get; set; } = "";

        [JsonProperty("fields")]
        public List<FieldDef> Fields { get; set; } = new List<FieldDef>();

        [JsonProperty("items")]
        public List<ItemRecord> Items { get; set; } = new List<ItemRecord>();

        [JsonProperty("overrides")]
        public List<OverrideKey> EnabledOverrides { get; set; } = new List<OverrideKey>();

        // ── Field management ─────────────────────────────────────────
        public void AddField(FieldDef field)
        {
            if (Fields.Any(f => f.Name == field.Name)) return; // no duplicates
            Fields.Add(field);
            // Initialize default values for all existing items
            foreach (var item in Items)
            {
                if (!item.Values.ContainsKey(field.Name))
                {
                    item.SetValue(field, (field.Min + field.Max) / 2.0);
                }
            }
        }

        public void RemoveField(string fieldName)
        {
            Fields.RemoveAll(f => f.Name == fieldName);
            foreach (var item in Items)
                item.Values.Remove(fieldName);
            EnabledOverrides.RemoveAll(o => o.FieldName == fieldName);
        }

        // ── Item management ──────────────────────────────────────────
        public void AddItem(string name)
        {
            var item = new ItemRecord { Name = name };
            foreach (var field in Fields)
            {
                item.SetValue(field, (field.Min + field.Max) / 2.0);
            }
            Items.Add(item);
        }

        public void AddItems(int count, string prefix = "Item")
        {
            int start = Items.Count + 1;
            for (int i = 0; i < count; i++)
            {
                AddItem($"{prefix} {start + i}");
            }
        }

        public void RemoveItem(int index)
        {
            if (index < 0 || index >= Items.Count) return;
            Items.RemoveAt(index);
            // Re-index overrides that reference items after the removed one
            EnabledOverrides.RemoveAll(o => o.ItemIndex == index);
            foreach (var o in EnabledOverrides.Where(o => o.ItemIndex > index))
                o.ItemIndex--;
        }

        public void DuplicateItem(int index)
        {
            if (index < 0 || index >= Items.Count) return;
            var clone = Items[index].Clone();
            clone.Name = $"{clone.Name} (copy)";
            Items.Insert(index + 1, clone);
            // Shift override indices
            foreach (var o in EnabledOverrides.Where(o => o.ItemIndex > index))
                o.ItemIndex++;
        }

        // ── Override management ──────────────────────────────────────
        public bool IsOverrideEnabled(string fieldName, int itemIndex = -1)
        {
            return EnabledOverrides.Any(o => o.FieldName == fieldName && o.ItemIndex == itemIndex);
        }

        public void ToggleOverride(string fieldName, int itemIndex = -1)
        {
            var existing = EnabledOverrides.FirstOrDefault(o =>
                o.FieldName == fieldName && o.ItemIndex == itemIndex);
            if (existing != null)
                EnabledOverrides.Remove(existing);
            else
                EnabledOverrides.Add(new OverrideKey { FieldName = fieldName, ItemIndex = itemIndex });
        }

        /// <summary>Get all enabled overrides as input param names.</summary>
        public List<(OverrideKey Key, string ParamName)> GetOverrideParamNames()
        {
            return EnabledOverrides
                .Select(o => (o, o.ToParamName(this)))
                .ToList();
        }

        // ── JSON serialization ───────────────────────────────────────
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        public static DataNodeSchema FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new DataNodeSchema();
            try
            {
                return JsonConvert.DeserializeObject<DataNodeSchema>(json) ?? new DataNodeSchema();
            }
            catch
            {
                return new DataNodeSchema();
            }
        }

        /// <summary>
        /// Structural equality check (field definitions only, not item values).
        /// Used to detect schema changes that require param rebuild.
        /// </summary>
        public bool SchemaEquals(DataNodeSchema other)
        {
            if (other is null) return false;
            if (Fields.Count != other.Fields.Count) return false;
            if (EnabledOverrides.Count != other.EnabledOverrides.Count) return false;
            for (int i = 0; i < Fields.Count; i++)
            {
                if (!Fields[i].Equals(other.Fields[i])) return false;
            }
            for (int i = 0; i < EnabledOverrides.Count; i++)
            {
                if (!EnabledOverrides[i].Equals(other.EnabledOverrides[i])) return false;
            }
            return true;
        }
    }
}
