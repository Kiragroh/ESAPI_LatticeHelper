using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace LatticeGenerator
{
    public sealed class LatticeStructureOption
    {
        public string Id { get; set; }
        public double VolumeCc { get; set; }

        public string DisplayName
        {
            get { return Id + "  (" + VolumeCc.ToString("F1", CultureInfo.InvariantCulture) + " cc)"; }
        }
    }

    public sealed class LatticeDialogSelection
    {
        public string TargetId { get; set; }
        public IReadOnlyList<string> AvoidanceStructureIds { get; set; }
        public LatticeParameters Parameters { get; set; }
    }

    public sealed class LatticeDialog : Window
    {
        private static readonly Brush WindowBackground = BrushFromHex("#F3F6F7");
        private static readonly Brush Surface = BrushFromHex("#FFFFFF");
        private static readonly Brush TextPrimary = BrushFromHex("#17252E");
        private static readonly Brush TextMuted = BrushFromHex("#53636D");
        private static readonly Brush ControlBorder = BrushFromHex("#A9B5BC");
        private static readonly Brush BorderLight = BrushFromHex("#D9E1E5");
        private static readonly Brush Accent = BrushFromHex("#087E73");
        private static readonly Brush AccentDark = BrushFromHex("#05665E");
        private static readonly Brush AccentSoft = BrushFromHex("#DDF2EE");
        private static readonly Brush DisabledBackground = BrushFromHex("#E7ECEF");
        private static readonly Brush Error = BrushFromHex("#B42318");
        private static readonly Brush ErrorSoft = BrushFromHex("#FDE7E5");

        private readonly IReadOnlyList<LatticeStructureOption> _allStructures;
        private readonly ComboBox _targetCombo;
        private readonly ListBox _avoidanceList;
        private readonly CheckBox _layerStructuresCheckBox;
        private TextBlock _validationSummary;
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private readonly Dictionary<TextBox, string> _fieldLabels = new Dictionary<TextBox, string>();
        private readonly Dictionary<TextBox, string> _fieldToolTips = new Dictionary<TextBox, string>();

        public LatticeDialog(
            IEnumerable<LatticeStructureOption> targets,
            IEnumerable<LatticeStructureOption> structures)
        {
            IReadOnlyList<LatticeStructureOption> targetList = targets
                .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _allStructures = structures
                .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Title = "LATTICE Geometry Generator";
            Background = WindowBackground;
            Foreground = TextPrimary;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13.0;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            SizeToContent = SizeToContent.Manual;
            Width = Math.Min(780.0, Math.Max(600.0, SystemParameters.WorkArea.Width - 100.0));
            Height = Math.Min(880.0, Math.Max(640.0, SystemParameters.WorkArea.Height - 80.0));
            MinWidth = Math.Min(600.0, Width);
            MinHeight = Math.Min(620.0, Height);
            MaxHeight = SystemParameters.WorkArea.Height;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            AddExplicitTheme();

            var root = new Grid { Background = WindowBackground };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement header = CreateHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var form = new StackPanel
            {
                Margin = new Thickness(28.0, 20.0, 28.0, 24.0),
                MaxWidth = 820.0
            };

            form.Children.Add(CreateSectionHeader("1", "Target"));
            form.Children.Add(CreateHelpText(
                "Select a non-empty GTV. Target size is reported for review but is not used as a hidden eligibility filter."));

            _targetCombo = new ComboBox
            {
                ItemsSource = targetList,
                DisplayMemberPath = nameof(LatticeStructureOption.DisplayName),
                SelectedIndex = targetList.Count > 0 ? 0 : -1,
                Margin = new Thickness(0.0, 8.0, 0.0, 4.0)
            };
            _targetCombo.SelectionChanged += TargetSelectionChanged;
            form.Children.Add(_targetCombo);

            form.Children.Add(CreateSectionHeader("2", "Automatic peak / valley grid"));
            form.Children.Add(CreateHelpText(
                "The script fills a three-dimensional checkerboard automatically. Hot and cold positions alternate in X, Y, and Z; no completed point set is trimmed to force a volume ratio."));

            var geometryGrid = CreateFormGrid();
            AddFormRow(
                geometryGrid,
                "Hot / cold spot diameter (cm)",
                CreateNumberField(nameof(LatticeParameters.DiameterCm), "1.5"),
                "Physical diameter of every spherical peak and valley spot. The reference dataset uses approximately 1.5 cm.");
            AddFormRow(
                geometryGrid,
                "Grid spacing (cm)",
                CreateNumberField(nameof(LatticeParameters.SeparationCm), "3.0"),
                "Distance between adjacent hot and cold centers. The reference grid uses 3.0 cm.");
            AddFormRow(
                geometryGrid,
                "Grid tilt around left-right axis (deg)",
                CreateNumberField(nameof(LatticeParameters.TiltDegrees), "0.0"),
                "Use 0 degrees for the axis-aligned reference layout. Allowed range: -45 to 45 degrees.");
            AddFormRow(
                geometryGrid,
                "Hot-spot clearance from target border (cm)",
                CreateNumberField(nameof(LatticeParameters.HotBorderClearanceCm), "0.6"),
                "Additional surface-to-border clearance for hot spots. The sphere radius is added automatically.");
            AddFormRow(
                geometryGrid,
                "Cold-spot envelope expansion (cm)",
                CreateNumberField(nameof(LatticeParameters.ColdEnvelopeExpansionCm), "0.5"),
                "Expands the selected GTV for cold-spot centers, approximating the broader low-dose PTV envelope.");
            AddFormRow(
                geometryGrid,
                "Minimum cold-spot volume inside GTV (%)",
                CreateNumberField(nameof(LatticeParameters.MinimumColdTargetOverlapPercent), "50"),
                "Cold spots below this estimated target overlap are omitted. The script uses deterministic three-dimensional sampling within each sphere; the default is 50%.");
            form.Children.Add(geometryGrid);

            form.Children.Add(CreateSectionHeader("3", "Hot-spot protection"));
            form.Children.Add(CreateHelpText(
                "Select PRV or OAR protection structures. Only hot positions are rejected here; alternating cold spots remain available near the protected region."));

            _avoidanceList = new ListBox
            {
                SelectionMode = SelectionMode.Multiple,
                DisplayMemberPath = nameof(LatticeStructureOption.DisplayName),
                MinHeight = 170.0,
                MaxHeight = 260.0,
                Margin = new Thickness(0.0, 8.0, 0.0, 12.0)
            };
            form.Children.Add(_avoidanceList);

            var avoidanceGrid = CreateFormGrid();
            AddFormRow(
                avoidanceGrid,
                "Additional clearance beyond selected PRV/OAR (cm)",
                CreateNumberField(nameof(LatticeParameters.ProtectionClearanceCm), "0.0"),
                "The spot radius is always included. Select an existing PRV+margin structure with 0.0 cm, or enter an extra margin when selecting the original organ.");
            form.Children.Add(avoidanceGrid);

            form.Children.Add(CreateSectionHeader("4", "Output"));
            form.Children.Add(CreateHelpText(
                "The script first reports the automatically detected hot/cold counts, all omitted positions, occupied grid planes, output mode, and analytical hot-volume ratio. Generation continues only after confirmation."));

            var outputGrid = CreateFormGrid();
            AddFormRow(
                outputGrid,
                "Hot-spot dose label for structure IDs (Gy)",
                CreateNumberField(nameof(LatticeParameters.HotDoseLabelGy), "50"),
                "Naming only; this script does not prescribe or optimize dose. Allowed whole numbers: 1 to 99 Gy.");
            form.Children.Add(outputGrid);

            _layerStructuresCheckBox = CreateCheckBox(
                "Create one hot and one cold structure per occupied grid plane",
                "One structure is created per grid plane, never per individual spot. If the Eclipse limit of 99 structures leaves too few slots, the script reports this and automatically switches to one combined hot and one combined cold structure.");
            _layerStructuresCheckBox.IsChecked = true;
            _layerStructuresCheckBox.Margin = new Thickness(0.0, 12.0, 0.0, 4.0);
            form.Children.Add(_layerStructuresCheckBox);

            var scrollViewer = new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false
            };
            Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            UIElement footer = CreateFooter();
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            RefreshAvoidanceItems();
        }

        public LatticeDialogSelection Selection { get; private set; }

        private UIElement CreateHeader()
        {
            var border = new Border
            {
                Background = Surface,
                BorderBrush = BorderLight,
                BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
                Padding = new Thickness(28.0, 20.0, 28.0, 18.0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5.0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18.0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            var accent = new Border
            {
                Background = Accent,
                CornerRadius = new CornerRadius(2.0)
            };
            Grid.SetColumn(accent, 0);
            grid.Children.Add(accent);

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = "LATTICE Geometry Generator",
                FontSize = 23.0,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary
            });
            text.Children.Add(new TextBlock
            {
                Text = "Automatic peak / valley placement with PRV-aware hot-spot protection",
                Foreground = TextMuted,
                Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            border.Child = grid;
            return border;
        }

        private UIElement CreateFooter()
        {
            var border = new Border
            {
                Background = Surface,
                BorderBrush = BorderLight,
                BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0),
                Padding = new Thickness(28.0, 14.0, 28.0, 16.0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _validationSummary = new TextBlock
            {
                Foreground = Error,
                Background = ErrorSoft,
                Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0.0, 0.0, 18.0, 0.0)
            };
            Grid.SetColumn(_validationSummary, 0);
            grid.Children.Add(_validationSummary);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 94.0,
                Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
                IsCancel = true
            };
            var generate = new Button
            {
                Content = "Analyze and generate",
                MinWidth = 178.0,
                Background = Accent,
                BorderBrush = Accent,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                IsDefault = true
            };
            generate.Click += GenerateClicked;
            buttons.Children.Add(cancel);
            buttons.Children.Add(generate);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            border.Child = grid;
            return border;
        }

        private UIElement CreateSectionHeader(string number, string title)
        {
            var grid = new Grid { Margin = new Thickness(0.0, 24.0, 0.0, 7.0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34.0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            var numberBlock = new Border
            {
                Width = 26.0,
                Height = 26.0,
                Background = AccentSoft,
                BorderBrush = Accent,
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(3.0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = number,
                    Foreground = AccentDark,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            grid.Children.Add(numberBlock);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 16.0,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 1);
            grid.Children.Add(titleBlock);
            return grid;
        }

        private TextBlock CreateHelpText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20.0
            };
        }

        private Grid CreateFormGrid()
        {
            var grid = new Grid { Margin = new Thickness(0.0, 8.0, 0.0, 0.0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0, GridUnitType.Star),
                MinWidth = 250.0
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(180.0)
            });
            return grid;
        }

        private void AddFormRow(Grid grid, string label, TextBox field, string toolTip)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 6.0, 18.0, 6.0),
                ToolTip = toolTip
            };
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            field.Margin = new Thickness(0.0, 4.0, 0.0, 4.0);
            field.ToolTip = toolTip;
            _fieldLabels[field] = label;
            _fieldToolTips[field] = toolTip;
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }

        private TextBox CreateNumberField(string propertyName, string value, bool isEnabled = true)
        {
            var field = new TextBox
            {
                Text = value,
                IsEnabled = isEnabled,
                HorizontalContentAlignment = HorizontalAlignment.Right
            };
            _fields[propertyName] = field;
            return field;
        }

        private CheckBox CreateCheckBox(string text, string toolTip)
        {
            return new CheckBox
            {
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = TextPrimary
                },
                ToolTip = toolTip
            };
        }

        private void TargetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshAvoidanceItems();
        }

        private void RefreshAvoidanceItems()
        {
            if (_avoidanceList == null)
            {
                return;
            }

            var selectedIds = _avoidanceList.SelectedItems
                .Cast<LatticeStructureOption>()
                .Select(item => item.Id)
                .ToList();
            var selectedTarget = _targetCombo == null
                ? null
                : _targetCombo.SelectedItem as LatticeStructureOption;

            _avoidanceList.ItemsSource = _allStructures
                .Where(item => selectedTarget == null ||
                    !string.Equals(item.Id, selectedTarget.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (LatticeStructureOption item in _avoidanceList.Items)
            {
                if (selectedIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                {
                    _avoidanceList.SelectedItems.Add(item);
                }
            }
        }

        private void GenerateClicked(object sender, RoutedEventArgs e)
        {
            LatticeDialogSelection selection;
            if (!TryCreateSelection(out selection))
            {
                return;
            }

            Selection = selection;
            DialogResult = true;
        }

        private bool TryCreateSelection(out LatticeDialogSelection selection)
        {
            selection = null;
            ClearValidationAppearance();

            var parseErrors = new Dictionary<string, string>();
            var parameters = new LatticeParameters
            {
                DiameterCm = ReadNumber(nameof(LatticeParameters.DiameterCm), parseErrors),
                SeparationCm = ReadNumber(nameof(LatticeParameters.SeparationCm), parseErrors),
                TiltDegrees = ReadNumber(nameof(LatticeParameters.TiltDegrees), parseErrors),
                HotBorderClearanceCm = ReadNumber(nameof(LatticeParameters.HotBorderClearanceCm), parseErrors),
                ColdEnvelopeExpansionCm = ReadNumber(nameof(LatticeParameters.ColdEnvelopeExpansionCm), parseErrors),
                ProtectionClearanceCm = ReadNumber(nameof(LatticeParameters.ProtectionClearanceCm), parseErrors),
                MinimumColdTargetOverlapPercent = ReadNumber(nameof(LatticeParameters.MinimumColdTargetOverlapPercent), parseErrors),
                HotDoseLabelGy = ReadNumber(nameof(LatticeParameters.HotDoseLabelGy), parseErrors),
                CreateLayerStructures = _layerStructuresCheckBox.IsChecked == true
            };

            var validation = LatticeGeometryMath.Validate(parameters);
            var allErrors = new Dictionary<string, string>(parseErrors);
            foreach (var error in validation.Errors)
            {
                if (!allErrors.ContainsKey(error.Key))
                {
                    allErrors[error.Key] = error.Value;
                }
            }

            LatticeStructureOption target = _targetCombo.SelectedItem as LatticeStructureOption;
            if (target == null)
            {
                allErrors["Target"] = "Select a target structure.";
                _targetCombo.BorderBrush = Error;
                _targetCombo.BorderThickness = new Thickness(2.0);
            }

            if (allErrors.Count > 0)
            {
                ShowValidationErrors(allErrors);
                return false;
            }

            selection = new LatticeDialogSelection
            {
                TargetId = target.Id,
                AvoidanceStructureIds = _avoidanceList.SelectedItems
                    .Cast<LatticeStructureOption>()
                    .Select(item => item.Id)
                    .ToList(),
                Parameters = parameters
            };
            return true;
        }

        private double ReadNumber(string propertyName, IDictionary<string, string> errors)
        {
            TextBox field = _fields[propertyName];
            double value;
            if (LatticeGeometryMath.TryParseFlexibleDouble(field.Text, out value))
            {
                return value;
            }

            errors[propertyName] = _fieldLabels[field] + " must contain a valid number.";
            return double.NaN;
        }

        private void ShowValidationErrors(IDictionary<string, string> errors)
        {
            foreach (var error in errors)
            {
                TextBox field;
                if (_fields.TryGetValue(error.Key, out field))
                {
                    field.BorderBrush = Error;
                    field.BorderThickness = new Thickness(2.0);
                    field.ToolTip = error.Value;
                }
            }

            _validationSummary.Text = "Please correct the highlighted input" +
                (errors.Count == 1 ? "." : "s.");
            _validationSummary.Visibility = Visibility.Visible;
        }

        private void ClearValidationAppearance()
        {
            foreach (var field in _fields.Values)
            {
                field.ClearValue(Control.BorderBrushProperty);
                field.ClearValue(Control.BorderThicknessProperty);
                field.ToolTip = _fieldToolTips.ContainsKey(field)
                    ? _fieldToolTips[field]
                    : "Enter a numeric value.";
            }

            if (_targetCombo != null)
            {
                _targetCombo.ClearValue(Control.BorderBrushProperty);
                _targetCombo.ClearValue(Control.BorderThicknessProperty);
            }

            if (_validationSummary != null)
            {
                _validationSummary.Visibility = Visibility.Collapsed;
            }
        }

        private void AddExplicitTheme()
        {
            Resources[typeof(TextBlock)] = CreateTextBlockStyle();
            Resources[typeof(TextBox)] = CreateTextBoxStyle();
            Resources[typeof(ComboBox)] = CreateComboBoxStyle();
            Resources[typeof(ComboBoxItem)] = CreateSelectorItemStyle(typeof(ComboBoxItem));
            Resources[typeof(ListBox)] = CreateListBoxStyle();
            Resources[typeof(ListBoxItem)] = CreateSelectorItemStyle(typeof(ListBoxItem));
            Resources[typeof(CheckBox)] = CreateCheckBoxStyle();
            Resources[typeof(Button)] = CreateButtonStyle();
            Resources[typeof(ToolTip)] = CreateToolTipStyle();
            Resources[typeof(ScrollBar)] = CreateScrollBarStyle();
        }

        private Style CreateTextBlockStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI")));
            return style;
        }

        private Style CreateTextBoxStyle()
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9.0, 6.0, 9.0, 6.0)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 34.0));
            style.Setters.Add(new Setter(TextBox.CaretBrushProperty, TextPrimary));
            style.Setters.Add(new Setter(TextBox.SelectionBrushProperty, AccentSoft));
            style.Setters.Add(new Setter(TextBox.SelectionTextBrushProperty, TextPrimary));

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.BackgroundProperty, DisabledBackground));
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, TextMuted));
            disabled.Setters.Add(new Setter(Control.BorderBrushProperty, BorderLight));
            style.Triggers.Add(disabled);
            return style;
        }

        private Style CreateComboBoxStyle()
        {
            var style = new Style(typeof(ComboBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8.0, 5.0, 8.0, 5.0)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 36.0));
            return style;
        }

        private Style CreateListBoxStyle()
        {
            var style = new Style(typeof(ListBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4.0)));
            return style;
        }

        private Style CreateSelectorItemStyle(Type targetType)
        {
            var style = new Style(targetType);
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8.0, 6.0, 8.0, 6.0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            var selected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, AccentSoft));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Triggers.Add(selected);
            return style;
        }

        private Style CreateCheckBoxStyle()
        {
            var style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateCheckBoxTemplate()));
            return style;
        }

        private ControlTemplate CreateCheckBoxTemplate()
        {
            var template = new ControlTemplate(typeof(CheckBox));
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

            var box = new FrameworkElementFactory(typeof(Border));
            box.Name = "CheckBoxBorder";
            box.SetValue(DockPanel.DockProperty, Dock.Left);
            box.SetValue(FrameworkElement.WidthProperty, 19.0);
            box.SetValue(FrameworkElement.HeightProperty, 19.0);
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(0.0, 1.0, 9.0, 0.0));
            box.SetValue(Border.BackgroundProperty, Surface);
            box.SetValue(Border.BorderBrushProperty, ControlBorder);
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(2.0));

            var mark = new FrameworkElementFactory(typeof(TextBlock));
            mark.Name = "CheckMark";
            mark.SetValue(TextBlock.TextProperty, "\u2713");
            mark.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            mark.SetValue(TextBlock.FontSizeProperty, 14.0);
            mark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            mark.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(mark);
            dock.AppendChild(box);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dock.AppendChild(content);
            template.VisualTree = dock;

            var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "CheckBoxBorder"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "CheckBoxBorder"));
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
            template.Triggers.Add(checkedTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
            template.Triggers.Add(disabledTrigger);
            return template;
        }

        private Style CreateButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16.0, 8.0, 16.0, 8.0)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 38.0));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            return style;
        }

        private Style CreateToolTipStyle()
        {
            var style = new Style(typeof(ToolTip));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ControlBorder));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10.0, 7.0, 10.0, 7.0)));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
            return style;
        }

        private Style CreateScrollBarStyle()
        {
            var style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(Control.BackgroundProperty, DisabledBackground));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Accent));
            return style;
        }

        private static Brush BrushFromHex(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
    }
}
