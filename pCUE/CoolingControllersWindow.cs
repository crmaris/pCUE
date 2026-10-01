using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Pcue.Cooling;

namespace pCUE
{
    public sealed class CoolingOutputRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public int Channel { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public bool CanControl { get; set; }
        public string Rpm { get; set; }
        public string HardwareSetting { get; set; }
        public int Minimum { get; set; }
        public int Percent { get; set; }
        string role = "Fan";
        public string Role { get { return role; } set { if (value != "Fan" && value != "Pump") return; if (role != value && value == "Pump") { Minimum = Math.Max(60, Minimum); Percent = 100; } role = value; Changed(); } }
        public void Changed() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(null)); }
    }
    public sealed class CoolingControllerRecovery
    {
        public CoolingUsbInfo Device { get; set; }
        public CoreBaseline Baseline { get; set; }
    }
    public sealed class CoolingControllerSetup
    {
        public string DeviceId { get; set; }
        public CoolingOutputRow[] Outputs { get; set; }
    }
    public sealed class CoolingControllersWindow : Window
    {
        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pCUE", "controllers");
        static readonly string RecoveryPath = Path.Combine(Folder, "recovery.json");
        static readonly string SetupPath = Path.Combine(Folder, "setup.json");
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly ObservableCollection<CoolingOutputRow> rows = new ObservableCollection<CoolingOutputRow>();
        readonly ComboBox devices = new ComboBox { MinWidth = 350, FontSize = 15 };
        readonly DataGrid grid = new DataGrid();
        readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12) };
        readonly Button connect, apply, restore, refresh;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        CoreController controller;
        CoolingUsbInfo selected;
        CoolingControllerRecovery recovery;
        CoolingControllerSetup saved;
        int[] previous;
        bool busy, closing, writing;
        readonly bool isPreview;
        public bool PendingRecovery { get { return !isPreview && (recovery != null || File.Exists(RecoveryPath)); } }
        public bool Busy { get { return busy; } }
        public CoolingControllersWindow(bool preview = false)
        {
            isPreview = preview;
            Title = "pCUE · Cooling controllers"; Width = 1000; Height = 710; MinWidth = 850; MinHeight = 570;
            Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)); Foreground = Brushes.WhiteSmoke; FontFamily = new FontFamily("Segoe UI"); FontSize = 15;
            var layout = new Grid { Margin = new Thickness(24) }; Content = new Border { Background = Background, Child = layout };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new TextBlock { Text = "Your fan and pump controllers", FontSize = 27, FontWeight = FontWeights.SemiBold }; layout.Children.Add(title);
            var choice = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) }; Grid.SetRow(choice, 1); layout.Children.Add(choice); choice.Children.Add(devices);
            connect = ActionButton("Connect", Connect); refresh = ActionButton("Refresh", Discover); choice.Children.Add(connect); choice.Children.Add(refresh);
            Grid.SetRow(status, 2); layout.Children.Add(status);
            grid.AutoGenerateColumns = false; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false; grid.RowHeaderWidth = 0; grid.MinRowHeight = 45; grid.FontSize = 15; grid.Foreground = Brushes.Black; grid.Background = Brushes.White; grid.ItemsSource = rows;
            var cellStyle = new Style(typeof(DataGridCell)); cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4))); cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center)); grid.CellStyle = cellStyle;
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Use", Binding = new Binding("Enabled"), Width = 55 });
            TextColumn("Output", "Name", 195, true); TextColumn("RPM", "Rpm", 80, true); TextColumn("Controller setting", "HardwareSetting", 135, true);
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Type", ItemsSource = new[] { "Fan", "Pump" }, SelectedItemBinding = new Binding("Role"), Width = 105 });
            TextColumn("Min %", "Minimum", 75, false); TextColumn("Manual %", "Percent", 105, false);
            Grid.SetRow(grid, 3); layout.Children.Add(grid);
            var instructions = new TextBlock { Text = "Identify the outputs before enabling them. Pump setup defaults to 100% with a 60% minimum.\nManual commands use percentages; stored firmware curves and unselected outputs are preserved.\nCORE/XT control is available only when firmware settings can be captured and verified.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16), Foreground = Brushes.LightGray }; Grid.SetRow(instructions, 4); layout.Children.Add(instructions);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetRow(actions, 5); layout.Children.Add(actions);
            apply = ActionButton("Apply selected outputs", Apply); restore = ActionButton("Stop / restore", Restore); actions.Children.Add(apply); actions.Children.Add(restore);
            timer.Tick += async delegate { if (!busy && controller != null) await Poll(); };
            Closing += ClosingWindow; Closed += delegate { timer.Stop(); if (controller != null) controller.Dispose(); };
            if (preview)
            {
                for (int i = 0; i < 7; i++) rows.Add(new CoolingOutputRow { Channel = i, Name = i == 0 ? "AIO / EXT pump" : "Fan " + i, Role = i == 0 ? "Pump" : "Fan", Percent = i == 0 ? 100 : 40, Minimum = i == 0 ? 60 : 30, Rpm = (1000 + i * 100).ToString(), HardwareSetting = "Firmware curve", CanControl = true });
                devices.Items.Add("Commander CORE · Example controller"); devices.SelectedIndex = 0; status.Text = "PREVIEW · Example readings · No hardware opened"; connect.IsEnabled = refresh.IsEnabled = apply.IsEnabled = restore.IsEnabled = false;
            }
            else Loaded += async delegate { await Discover(); timer.Start(); };
        }
        void TextColumn(string header, string path, int width, bool readOnly) { grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = width, IsReadOnly = readOnly }); }
        Button ActionButton(string text, Func<Task> action) { var button = new Button { Content = text, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(12, 0, 0, 0), Foreground = Brushes.Black, FontSize = 15 }; button.Click += async delegate { if (!busy) await action(); }; return button; }
        static void Save(string path, string data)
        {
            Directory.CreateDirectory(Folder); string temporary = path + ".new"; File.WriteAllText(temporary, data); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        void Buttons()
        {
            connect.IsEnabled = !busy && controller == null && !PendingRecovery; refresh.IsEnabled = !busy && controller == null && !PendingRecovery; devices.IsEnabled = !busy && controller == null && !PendingRecovery;
            apply.IsEnabled = !busy && controller != null && rows.Any(r => r.CanControl) && (recovery != null || !File.Exists(RecoveryPath)); restore.IsEnabled = !busy && PendingRecovery; grid.IsEnabled = !writing;
        }
        async Task Discover()
        {
            busy = true; Buttons();
            try
            {
                try { saved = File.Exists(SetupPath) ? json.Deserialize<CoolingControllerSetup>(File.ReadAllText(SetupPath)) : null; } catch { saved = null; }
                if (File.Exists(RecoveryPath)) { recovery = json.Deserialize<CoolingControllerRecovery>(File.ReadAllText(RecoveryPath)); status.Text = "Saved controller recovery must be restored before starting."; return; }
                var found = await Task.Run(CoolingUsbDiscovery.Find); devices.ItemsSource = found;
                if (saved != null && saved.DeviceId != null) devices.SelectedItem = found.FirstOrDefault(d => d.Id == saved.DeviceId);
                else if (found.Length == 1) devices.SelectedIndex = 0;
                status.Text = found.Length == 0 ? "No supported CORE/XT controller found." : devices.SelectedItem == null ? "Choose the controller to use. A missing saved controller is never substituted." : "Ready to connect. New outputs remain unchecked.";
            } catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; Buttons(); }
        }
        async Task Connect()
        {
            selected = devices.SelectedItem as CoolingUsbInfo; if (selected == null) { status.Text = "Choose a detected controller first."; return; }
            busy = true; Buttons();
            try
            {
                controller = await Task.Run(delegate { return CoolingUsbDiscovery.Open(selected); }); var state = await Task.Run(controller.Read); rows.Clear();
                for (int i = 0; i < controller.Count; i++)
                {
                    var old = saved != null && saved.DeviceId == selected.Id && saved.Outputs != null ? saved.Outputs.FirstOrDefault(r => r.Channel == i) : null;
                    rows.Add(new CoolingOutputRow { Channel = i, Name = controller.HasExt ? (i == 0 ? "AIO / EXT pump" : "Fan " + i) : "Fan " + (i + 1), Minimum = old == null ? 30 : old.Minimum, Percent = old == null ? 40 : old.Percent, Role = old == null ? "Fan" : old.Role, Enabled = old != null && old.Enabled });
                    if (old != null) { rows.Last().Minimum = old.Minimum; rows.Last().Percent = old.Percent; }
                }
                Present(state); previous = Enumerable.Repeat(-1, controller.Count).ToArray();
                Save(SetupPath, json.Serialize(new CoolingControllerSetup { DeviceId = selected.Id, Outputs = rows.ToArray() }));
            } catch (Exception ex) { status.Text = ex.Message; if (controller != null) controller.Dispose(); controller = null; }
            finally { busy = false; Buttons(); }
        }
        void Present(CoreState state)
        {
            foreach (var r in rows) { r.Rpm = state.Rpm[r.Channel].HasValue ? state.Rpm[r.Channel].Value.ToString() : "—"; r.HardwareSetting = state.Writable ? state.Modes[r.Channel] == 0 ? state.Powers[r.Channel] + "% fixed" : "Firmware curve" : "Unavailable"; r.CanControl = state.Writable && state.Connected[r.Channel] && (state.Modes[r.Channel] == 0 || state.Modes[r.Channel] == 2); r.Changed(); }
            status.Text = state.Writable ? (recovery == null ? "Monitoring · " : "Managed outputs · ") + selected + " · Firmware " + state.Firmware : "Monitoring only: controller settings cannot be verified. " + state.ControlError;
        }
        async Task Poll()
        {
            busy = true; Buttons();
            try { Present(await Task.Run(controller.Read)); }
            catch (Exception ex) { status.Text = "Controller feedback lost. Recovery remains saved. " + ex.Message; if (recovery != null) try { await Task.Run(delegate { controller.Apply(recovery.Baseline.Selected.Select(v => v ? 100 : -1).ToArray(), Enumerable.Repeat(-1, controller.Count).ToArray(), null); }); } catch { } }
            finally { busy = false; Buttons(); }
        }
        async Task Apply()
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true) || HasInputError(grid)) { status.Text = "Correct the output values before applying."; return; }
            var managed = rows.Where(r => r.Enabled).ToArray();
            if (managed.Length == 0 || managed.Any(r => !r.CanControl || r.Minimum < 20 || r.Minimum > 100 || r.Percent < r.Minimum || r.Percent > 100 || (r.Role != "Fan" && r.Role != "Pump"))) { status.Text = "Enable verified outputs and use whole percentages within their minimum and 100%."; return; }
            bool[] mask = rows.Select(r => r.Enabled).ToArray();
            if (recovery != null && !mask.SequenceEqual(recovery.Baseline.Selected)) { status.Text = "Stop / restore before changing the selected outputs."; return; }
            busy = writing = true; Buttons();
            try
            {
                if (recovery == null) { var baseline = await Task.Run(delegate { return controller.Capture(mask); }); var next = new CoolingControllerRecovery { Device = selected, Baseline = baseline }; Save(RecoveryPath, json.Serialize(next)); recovery = next; }
                int[] desired = rows.Select(r => r.Enabled ? r.Percent : -1).ToArray(); await Task.Run(delegate { controller.Apply(desired, previous, null); }); previous = desired;
                Save(SetupPath, json.Serialize(new CoolingControllerSetup { DeviceId = selected.Id, Outputs = rows.ToArray() })); status.Text = "Selected output percentages applied and verified. Stop / restore returns the captured settings.";
            } catch (Exception ex) { status.Text = "Control failed; restore saved settings. " + ex.Message; }
            finally { busy = writing = false; Buttons(); }
        }
        async Task Restore()
        {
            busy = writing = true; Buttons();
            try
            {
                if (recovery == null) recovery = json.Deserialize<CoolingControllerRecovery>(File.ReadAllText(RecoveryPath));
                if (controller == null) { selected = recovery.Device; controller = await Task.Run(delegate { return CoolingUsbDiscovery.Open(selected); }); }
                await Task.Run(delegate { controller.Restore(recovery.Baseline); }); File.Delete(RecoveryPath); recovery = null; previous = null; controller.Dispose(); controller = null; rows.Clear(); status.Text = "Stopped · Saved controller settings restored. Connect to monitor again.";
            } catch (Exception ex) { status.Text = "Recovery incomplete. Saved settings are retained. " + ex.Message; }
            finally { busy = writing = false; Buttons(); }
        }
        async void ClosingWindow(object sender, CancelEventArgs e)
        {
            if (isPreview || closing) return;
            if (busy) { e.Cancel = true; status.Text = "Wait for the current controller operation before closing."; return; }
            if (!PendingRecovery) return;
            e.Cancel = true; await Restore(); if (!PendingRecovery) { closing = true; Close(); }
        }
        static bool HasInputError(DependencyObject node)
        {
            if (Validation.GetHasError(node)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasInputError(VisualTreeHelper.GetChild(node, i))) return true;
            return false;
        }
    }
}
