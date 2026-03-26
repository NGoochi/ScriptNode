using System;
using System.Collections.Generic;
using System.Linq;

using Eto.Drawing;
using Eto.Forms;

namespace ScriptNodePlugin
{
    /// <summary>
    /// Cross-platform Eto.Forms editor for DataNode.
    /// Dark theme matching ScriptNode's colour scheme with a blue accent.
    ///
    /// Key design decisions:
    /// - NON-MODAL (Form, not Dialog) so users can interact with the GH canvas
    /// - In-place content rebuild (no close/reopen) — fast updates
    /// - Singleton pattern: only one editor per DataNode instance at a time
    /// - Column headers + fields use fixed widths for alignment
    /// </summary>
    public class DataNodeEditor : Form
    {
        // ── Colours (matching ScriptNodeAttributes) ───────────
        private static readonly Color ColBg       = Color.FromArgb(58, 58, 58);
        private static readonly Color ColPanel    = Color.FromArgb(72, 72, 72);
        private static readonly Color ColText     = Color.FromArgb(224, 224, 224);
        private static readonly Color ColDimText  = Color.FromArgb(160, 160, 160);
        private static readonly Color ColAccent   = Color.FromArgb(34, 102, 204);  // Blue
        private static readonly Color ColInput    = Color.FromArgb(48, 48, 48);
        private static readonly Color ColItemBg   = Color.FromArgb(52, 52, 52);

        // ── Field widths for alignment ────────────────────────
        private const int W_NAME = 150;
        private const int W_TYPE = 75;
        private const int W_NUM  = 55;
        private const int W_DEC  = 42;
        private const int W_CHK  = 50;
        private const int W_BTN  = 30;

        // ── Singleton tracking ────────────────────────────────
        private static readonly Dictionary<Guid, DataNodeEditor> _openEditors
            = new Dictionary<Guid, DataNodeEditor>();

        // ── Instance state ────────────────────────────────────
        private readonly DataNodeComponent _component;
        private DataNodeSchema _schema;
        private readonly Panel _contentPanel;
        private string _filterText = "";
        private bool _isBuilding = false;  // Suppresses recompute during UI construction

        // ── Constructor ───────────────────────────────────────
        private DataNodeEditor(DataNodeComponent component)
        {
            _component = component;
            _schema = component.Schema;

            Title = "DataNode Editor";
            Size = new Size(720, 620);
            MinimumSize = new Size(580, 400);
            Resizable = true;
            BackgroundColor = ColBg;

            _contentPanel = new Panel { BackgroundColor = ColBg };
            Content = _contentPanel;

            RebuildContent();

            Closed += (s, e) =>
            {
                _openEditors.Remove(component.InstanceGuid);
                // Trigger param rebuild when editor closes (applies all changes)
                component.RequestRebuild();
            };
        }

        /// <summary>Show or focus the editor for a DataNode component.</summary>
        public static void ShowEditor(DataNodeComponent component)
        {
            if (_openEditors.TryGetValue(component.InstanceGuid, out var existing))
            {
                try { existing.BringToFront(); existing.Focus(); } catch { }
                return;
            }

            // Defer creation so it doesn't block the GH canvas paint/click thread
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                try
                {
                    var editor = new DataNodeEditor(component);
                    _openEditors[component.InstanceGuid] = editor;
                    editor.Show();  // NON-MODAL — canvas remains interactive
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine($"[DataNode] Editor failed to open: {ex.Message}");
                }
            });
        }

        // ── Full content rebuild (fast, in-place) ─────────────
        private void RebuildContent()
        {
            _isBuilding = true;  // Suppress recompute during construction
            try
            {
                var mainLayout = new DynamicLayout
                {
                    Padding = new Padding(12),
                    Spacing = new Size(8, 6),
                    BackgroundColor = ColBg,
                };

                // ── List Name ────────────────────────────────────
                mainLayout.AddRow(MakeListNameRow());
                mainLayout.AddRow(MakeSep());

                // ── Schema section ───────────────────────────────
                mainLayout.AddRow(MakeSchemaSection());
                mainLayout.AddRow(MakeSep());

                // ── Items section ────────────────────────────────
                mainLayout.AddRow(MakeItemsSection());
                mainLayout.AddRow(MakeSep());

                // ── Preset buttons ───────────────────────────────
                mainLayout.AddRow(MakePresetBar());

                // ── Bottom bar ───────────────────────────────────
                var applyBtn = MakeBtn("Apply & Refresh", "Apply all changes and rebuild node parameters", (s, e) =>
                {
                    _component.RequestRebuild();
                });
                var closeBtn = MakeBtn("Close", "Close this editor", (s, e) => Close());
                mainLayout.AddRow(new TableLayout(new TableRow(null, new TableCell(applyBtn), new TableCell(closeBtn))));

                _contentPanel.Content = new Scrollable
                {
                    Content = mainLayout,
                    BackgroundColor = ColBg,
                    ExpandContentWidth = true,
                };
            }
            finally
            {
                _isBuilding = false;
            }
        }

        // ── List Name Row ────────────────────────────────────
        private Control MakeListNameRow()
        {
            var label = new Label
            {
                Text = "List Name:",
                TextColor = ColText,
                Width = 75,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Custom name displayed on the node header. Leave blank for 'DataNode'.",
            };
            var nameBox = new TextBox
            {
                Text = _schema.ListName ?? "",
                PlaceholderText = "DataNode",
                BackgroundColor = ColInput,
                TextColor = ColText,
                ToolTip = "Custom name displayed on the node header. Leave blank for 'DataNode'.",
            };
            nameBox.TextChanged += (s, e) =>
            {
                if (_isBuilding) return;
                _schema.ListName = nameBox.Text ?? "";
                _component.RequestRecompute(); // updates canvas header live
            };

            var row = new TableLayout { Spacing = new Size(6, 0) };
            row.Rows.Add(new TableRow(
                new TableCell(label),
                new TableCell(nameBox) { ScaleWidth = true }
            ));
            return row;
        }

        // ── Schema Section ───────────────────────────────────
        private Control MakeSchemaSection()
        {
            var container = new DynamicLayout { Spacing = new Size(6, 3) };

            // Header row
            var addFieldBtn = MakeBtn("+ Add Field", "Add a new data field to the schema", (s, e) =>
            {
                _schema.AddField(new FieldDef
                {
                    Name = $"field_{_schema.Fields.Count + 1}",
                    TypeHint = "float", Min = 0, Max = 100, DecimalPlaces = 2,
                });
                RebuildContent();
            });

            var headerRow = new TableLayout { Spacing = new Size(6, 0) };
            headerRow.Rows.Add(new TableRow(
                new TableCell(MakeSectionLabel("Schema"), true),
                new TableCell(addFieldBtn)
            ));
            container.AddRow(headerRow);

            // Column headers — fixed widths matching field inputs
            var colRow = new TableLayout { Spacing = new Size(4, 0) };
            colRow.Rows.Add(new TableRow(
                MakeColCell("Name", W_NAME),
                MakeColCell("Type", W_TYPE),
                MakeColCell("Min", W_NUM),
                MakeColCell("Max", W_NUM),
                MakeColCell("Dec", W_DEC),
                MakeColCell("Parent", W_CHK),
                MakeColCell("", W_BTN)
            ));
            container.AddRow(colRow);

            // Field rows
            for (int fi = 0; fi < _schema.Fields.Count; fi++)
            {
                var field = _schema.Fields[fi];

                var nameBox = new TextBox
                {
                    Text = field.Name, BackgroundColor = ColInput, TextColor = ColText,
                    Width = W_NAME, ToolTip = "Field name (used as output parameter name)",
                };
                nameBox.TextChanged += (s, e) => { field.Name = nameBox.Text ?? ""; };

                var typeDD = MakeTypeDropdown(field);
                typeDD.Width = W_TYPE;
                typeDD.ToolTip = "Data type for this field";

                var minBox = new TextBox
                {
                    Text = field.Min.ToString("G"), BackgroundColor = ColInput, TextColor = ColText,
                    Width = W_NUM, ToolTip = "Minimum slider value",
                };
                minBox.TextChanged += (s, e) => { if (double.TryParse(minBox.Text, out double v)) field.Min = v; };

                var maxBox = new TextBox
                {
                    Text = field.Max.ToString("G"), BackgroundColor = ColInput, TextColor = ColText,
                    Width = W_NUM, ToolTip = "Maximum slider value",
                };
                maxBox.TextChanged += (s, e) => { if (double.TryParse(maxBox.Text, out double v)) field.Max = v; };

                var decBox = new NumericStepper
                {
                    Value = field.DecimalPlaces, MinValue = 0, MaxValue = 5, DecimalPlaces = 0,
                    BackgroundColor = ColInput, TextColor = ColText,
                    Width = W_DEC, ToolTip = "Decimal precision (0–5)",
                };
                decBox.ValueChanged += (s, e) => { field.DecimalPlaces = (int)decBox.Value; };

                var parentChk = new CheckBox
                {
                    Checked = field.IsParent,
                    ToolTip = "Parent field — wire override replaces ALL items",
                };
                parentChk.CheckedChanged += (s, e) => { field.IsParent = parentChk.Checked ?? false; };

                var removeBtn = MakeBtn("✕", "Remove this field from the schema", (s, e) =>
                {
                    _schema.RemoveField(field.Name);
                    RebuildContent();
                });
                removeBtn.Width = W_BTN;

                var row = new TableLayout { Spacing = new Size(4, 0) };
                row.Rows.Add(new TableRow(
                    new TableCell(nameBox),
                    new TableCell(typeDD),
                    new TableCell(minBox),
                    new TableCell(maxBox),
                    new TableCell(decBox),
                    new TableCell(parentChk),
                    new TableCell(removeBtn)
                ));
                container.AddRow(row);
            }

            if (_schema.Fields.Count == 0)
                container.AddRow(MakeDimLabel("No fields defined. Click '+ Add Field' to start."));

            return container;
        }

        // ── Items Section (GridView-based for performance) ────
        private Control MakeItemsSection()
        {
            var container = new DynamicLayout { Spacing = new Size(6, 3) };

            // Header buttons
            var addBtn = MakeBtn("+ Add", "Add a single new item", (s, e) =>
            {
                _schema.AddItem($"Item {_schema.Items.Count + 1}");
                RebuildContent();
            });
            var batchBtn = MakeBtn("+ Batch", "Add multiple items at once", (s, e) => ShowBatchDialog());
            var applyBtn = MakeBtn("Apply", "Apply changes and rebuild node parameters", (s, e) => _component.RequestRebuild());

            var headerRow = new TableLayout { Spacing = new Size(6, 0) };
            headerRow.Rows.Add(new TableRow(
                new TableCell(MakeSectionLabel($"Items ({_schema.Items.Count})"), true),
                new TableCell(addBtn),
                new TableCell(batchBtn),
                new TableCell(applyBtn)
            ));
            container.AddRow(headerRow);

            if (_schema.Fields.Count == 0)
            {
                container.AddRow(MakeDimLabel("Define fields in the Schema section first."));
                return container;
            }

            if (_schema.Items.Count == 0)
            {
                container.AddRow(MakeDimLabel("No items. Click '+ Add' or '+ Batch' to create."));
                return container;
            }

            // ── GridView: one row per item, columns for Name + each field value ──
            var grid = new GridView
            {
                AllowMultipleSelection = false,
                ShowHeader = true,
                Height = 360,
                BackgroundColor = ColPanel,
                GridLines = GridLines.Horizontal,
            };

            // Column 0: Item Name (editable)
            grid.Columns.Add(new GridColumn
            {
                HeaderText = "Name",
                Width = 100,
                Editable = true,
                DataCell = new TextBoxCell { Binding = Binding.Delegate<ItemRecord, string>(
                    r => r.Name,
                    (r, val) => { r.Name = val; }
                )},
            });

            // One column per field (editable numeric/text)
            foreach (var field in _schema.Fields)
            {
                var f = field; // capture for lambda
                grid.Columns.Add(new GridColumn
                {
                    HeaderText = $"{f.Name}\n[{f.Min}–{f.Max}]",
                    Width = 100,
                    Editable = true,
                    DataCell = new TextBoxCell { Binding = Binding.Delegate<ItemRecord, string>(
                        r =>
                        {
                            if (IsNumericType(f.TypeHint))
                                return r.GetNumericValue(f).ToString($"F{f.DecimalPlaces}");
                            return r.GetStringValue(f);
                        },
                        (r, val) =>
                        {
                            if (IsNumericType(f.TypeHint))
                            {
                                if (double.TryParse(val, out double parsed))
                                {
                                    parsed = Math.Max(f.Min, Math.Min(f.Max, parsed));
                                    parsed = Math.Round(parsed, f.DecimalPlaces);
                                    r.SetValue(f, parsed);
                                }
                            }
                            else
                            {
                                r.Values[f.Name] = val;
                            }
                            if (!_isBuilding)
                                _component.RequestRecompute();
                        }
                    )},
                });
            }

            // Action column: delete button
            grid.Columns.Add(new GridColumn
            {
                HeaderText = "",
                Width = 35,
                Editable = false,
                DataCell = new TextBoxCell { Binding = Binding.Delegate<ItemRecord, string>(
                    r => "🗑", (r, val) => { }
                )},
            });

            // Populate the data store
            var dataStore = new DataStoreCollection<ItemRecord>();
            foreach (var item in _schema.Items)
                dataStore.Add(item);
            grid.DataStore = dataStore;

            // Handle delete via cell click on the last column
            grid.CellClick += (s, e) =>
            {
                // Last column = delete
                if (e.Column == grid.Columns.Count - 1 && e.Row >= 0 && e.Row < _schema.Items.Count)
                {
                    _schema.RemoveItem(e.Row);
                    RebuildContent();
                }
            };

            container.AddRow(grid);

            return container;
        }

        // ── Preset Bar ───────────────────────────────────────
        private Control MakePresetBar()
        {
            var cells = new List<TableCell>();
            cells.Add(new TableCell(new Label { Text = "Presets:", TextColor = ColDimText, ToolTip = "Apply a slider range preset to all numeric fields" }));

            foreach (var preset in SliderPresets.All)
            {
                var name = preset.Name;
                var btn = MakeBtn($"{preset.Min}→{preset.Max}",
                    $"Set all numeric fields to {preset.Name} range ({preset.Min}–{preset.Max}, {preset.Decimals} dp)",
                    (s, e) =>
                    {
                        foreach (var f in _schema.Fields)
                            if (IsNumericType(f.TypeHint))
                                SliderPresets.Apply(f, name);
                        RebuildContent();
                    });
                cells.Add(new TableCell(btn));
            }
            cells.Add(new TableCell(null, true));

            var row = new TableLayout { Spacing = new Size(4, 0) };
            row.Rows.Add(new TableRow(cells.ToArray()));
            return row;
        }

        // ── Batch Add Dialog ─────────────────────────────────
        private void ShowBatchDialog()
        {
            var countStepper = new NumericStepper
            {
                Value = 10, MinValue = 1, MaxValue = 1000, DecimalPlaces = 0,
                BackgroundColor = ColInput, TextColor = ColText, Width = 80,
                ToolTip = "Number of items to create",
            };
            var prefixBox = new TextBox
            {
                Text = "Item", BackgroundColor = ColInput, TextColor = ColText, Width = 120,
                ToolTip = "Name prefix (items named 'Prefix 1', 'Prefix 2', etc.)",
            };

            var dlg = new Dialog<bool>
            {
                Title = "Batch Add Items",
                Size = new Size(320, 160),
                BackgroundColor = ColBg,
            };

            var layout = new DynamicLayout { Padding = new Padding(12), Spacing = new Size(8, 8) };
            layout.AddRow(new Label { Text = "Count:", TextColor = ColText }, countStepper);
            layout.AddRow(new Label { Text = "Prefix:", TextColor = ColText }, prefixBox);

            var addBtn = MakeBtn("Add Items", "Create the specified number of items", (s, e) =>
            {
                _schema.AddItems((int)countStepper.Value, prefixBox.Text);
                dlg.Close(true);
                RebuildContent();
            });
            var cancelBtn = MakeBtn("Cancel", "Cancel batch add", (s, e) => dlg.Close(false));
            layout.AddRow(null, cancelBtn, addBtn);

            dlg.Content = layout;
            dlg.ShowModal(this);
        }

        // ── Type Dropdown ────────────────────────────────────
        private static DropDown MakeTypeDropdown(FieldDef field)
        {
            var types = new[] { "float", "int", "str", "bool", "Point3d", "Vector3d", "Plane",
                "Line", "Curve", "Surface", "Brep", "Mesh", "color", "geometry" };
            var dd = new DropDown { BackgroundColor = ColInput, TextColor = ColText };
            foreach (var t in types) dd.Items.Add(t);
            dd.SelectedIndex = Math.Max(0, Array.IndexOf(types, field.TypeHint));
            dd.SelectedIndexChanged += (s, e) =>
            {
                if (dd.SelectedIndex >= 0) field.TypeHint = types[dd.SelectedIndex];
            };
            return dd;
        }

        // ── UI Helpers ───────────────────────────────────────
        private static Button MakeBtn(string text, string tooltip, EventHandler<EventArgs> handler)
        {
            var btn = new Button
            {
                Text = text,
                BackgroundColor = ColAccent,
                TextColor = Color.FromArgb(255, 255, 255),
                ToolTip = tooltip,
            };
            btn.Click += handler;
            return btn;
        }

        private static Label MakeSectionLabel(string text) => new Label
        {
            Text = text,
            Font = new Font(SystemFont.Bold, 11),
            TextColor = Color.FromArgb(255, 255, 255),
        };

        private static TableCell MakeColCell(string text, int width) => new TableCell(new Label
        {
            Text = text,
            TextColor = ColDimText,
            Font = new Font(SystemFont.Default, 8),
            Width = width,
        });

        private static Label MakeDimLabel(string text) => new Label { Text = text, TextColor = ColDimText };

        private static Label MakeItemIdx(string text) => new Label
        {
            Text = text,
            TextColor = ColAccent,
            Font = new Font(SystemFont.Bold, 9),
            Width = 30,
        };

        private static Panel MakeSep() => new Panel { Height = 1, BackgroundColor = Color.FromArgb(80, 80, 80) };

        private static bool IsNumericType(string typeHint)
        {
            switch (typeHint?.ToLowerInvariant())
            {
                case "float": case "double": case "number":
                case "int": case "integer":
                    return true;
                default:
                    return false;
            }
        }
    }
}
