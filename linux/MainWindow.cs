using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pCUE;
using Pcue.Cooling;

namespace Pcue.Linux;

public sealed class MainWindow : Window
{
    readonly bool preview;
    readonly LinuxTarget local;
    readonly NativeSession native;
    readonly PcueRemoteClient remote = new();
    readonly SemaphoreSlim operations = new(1, 1);
    readonly List<OutputRow> rows = new();
    readonly StackPanel outputRows = new() { Spacing = 8 };
    readonly ComboBox devices = new() { MinWidth = 280, MaxWidth = 450 };
    readonly ComboBox target = new() { ItemsSource = new[] { "This Linux PC", "Remote pCUE" }, SelectedIndex = 0, MinWidth = 140 };
    readonly TextBox host = new() { Watermark = "Remote host / IP", Width = 170 };
    readonly TextBox token = new() { Watermark = "Token (kept in memory)", PasswordChar = '●', Width = 190 };
    readonly NumericUpDown port = new() { Value = 5056, Minimum = 1, Maximum = 65535, Increment = 1, Width = 140 };
    readonly TextBlock status = new() { Text = "Choose a controller, then Connect. Outputs remain unchanged until Apply.", TextWrapping = TextWrapping.Wrap };
    readonly TextBlock telemetry = new() { Text = "CPU temperature: N/A   •   CPU frequency: N/A   •   CPU load: N/A", TextWrapping = TextWrapping.Wrap };
    readonly TextBlock guidance = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 12) };
    readonly Grid header = Header();
    readonly WrapPanel tachControls = new();
    readonly TextBlock tachStatus = new() { Text = "Tachometer: disconnected", TextWrapping = TextWrapping.Wrap };
    readonly ComboBox holdFan = new() { ItemsSource = new[] { "Fan 1", "Fan 2", "Fan 3", "Fan 4", "Fan 5", "Fan 6" }, SelectedIndex = 0, MinWidth = 85 };
    readonly NumericUpDown holdRpm = new() { Value = 1000, Minimum = 101, Maximum = 10000, Increment = 10, Width = 140 };
    readonly Button apply, restore;
    readonly Button applyModes;
    readonly DispatcherTimer timer;
    readonly FileStream instanceLock;
    readonly FileStream packageLock;
    readonly RemoteControlServer server;
    PcueStatusSnapshot remoteSnapshot;
    CoolingUsbInfo[] inventory = Array.Empty<CoolingUsbInfo>();
    bool remoteActive, nativeActive, allowClose;
    DateTime nextUpdateCheck = DateTime.UtcNow.AddSeconds(30);
    string pendingRelease;
    public MainWindow(bool preview)
    {
        this.preview = preview;
        Title = "pCUE Linux " + Program.Version + (preview ? " — offline preview" : ""); Width = 1120; Height = 880; MinWidth = 860; MinHeight = 620;
        if (!preview)
        {
            Directory.CreateDirectory(Program.StateFolder);
            instanceLock = new FileStream(Path.Combine(Program.StateFolder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (LinuxAutoUpdate.Managed) packageLock = new FileStream("/run/lock/pcue-linux.update.lock", FileMode.Open, FileAccess.Read, FileShare.None);
            local = new(); native = new(Program.StateFolder, recoveryMarker: LinuxAutoUpdate.Managed ? "/var/lib/pcue-linux/recovery/recovery-" + LinuxAutoUpdate.UserId : null);
            if (Program.Serve) { server = new RemoteControlServer(local, RemoteControlServer.DefaultPrefix, Environment.GetEnvironmentVariable("PCUE_TOKEN")); server.Start(); }
        }
        var root = new DockPanel { Margin = new Thickness(24), LastChildFill = true };
        var top = new StackPanel { Spacing = 14 }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var heading = new DockPanel(); var theme = Button("Light / dark", () => { Application.Current.RequestedThemeVariant = Application.Current.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark; return Task.CompletedTask; }); DockPanel.SetDock(theme, Dock.Right); heading.Children.Add(theme);
        heading.Children.Add(new TextBlock { Text = "Your fan and pump controls", FontSize = 26, FontWeight = FontWeight.SemiBold }); top.Children.Add(heading);
        top.Children.Add(new TextBlock { Text = "Commander PRO • CORE / XT • iCUE LINK • OCTO", Opacity = 0.75 });
        var connection = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { target, host, port, token, Button("Connect", Connect), Button("Disconnect", Disconnect) }) { control.Margin = new Thickness(0, 0, 10, 8); connection.Children.Add(control); }
        top.Children.Add(connection);
        var choice = new WrapPanel(); devices.Margin = new Thickness(0, 0, 12, 0); choice.Children.Add(devices); choice.Children.Add(Button("Refresh devices", RefreshDevices)); top.Children.Add(choice);
        top.Children.Add(status);
        top.Children.Add(guidance);
        var bottom = new StackPanel { Spacing = 12 }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        apply = Button("Apply selected outputs", Apply); apply.Margin = new Thickness(0, 0, 16, 8); actions.Children.Add(apply);
        restore = Button("Stop / restore", Restore); restore.Margin = new Thickness(0, 0, 16, 8); actions.Children.Add(restore);
        applyModes = Button("Apply selected fan modes", ApplyModes); actions.Children.Add(applyModes); bottom.Children.Add(actions);
        foreach (var control in new Control[] { Button("Connect tachometer", ConnectTach), holdFan, holdRpm, Button("Start RPM hold", StartHold), Button("Stop hold", StopHold) }) { control.Margin = new Thickness(0, 0, 10, 8); tachControls.Children.Add(control); }
        bottom.Children.Add(tachControls); bottom.Children.Add(tachStatus); bottom.Children.Add(telemetry);
        bottom.Children.Add(new TextBlock { Text = "Linux USB control is experimental. Stop other fan-control tools before connecting. Be quiet IO, motherboard PWM, acoustic acquisition leases and Windows services are unavailable in this Linux build.", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        var table = new StackPanel { Spacing = 10 };
        table.Children.Add(header); table.Children.Add(outputRows);
        root.Children.Add(new ScrollViewer { Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = root;
        remote.SnapshotReceived += (_, snapshot) => Dispatcher.UIThread.Post(() => { remoteSnapshot = snapshot; if (remoteActive) ShowSnapshot(snapshot); });
        remote.ConnectionChanged += (_, connectionState) => Dispatcher.UIThread.Post(() => { if (remoteActive) status.Text = connectionState.Message; });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += async (_, _) => await Poll();
        Opened += async (_, _) =>
        {
            if (preview) { SetPreviewMode(false); return; }
            if (Screens.Primary != null) Height = Math.Min(Height, Screens.Primary.WorkingArea.Height / RenderScaling - 40);
            await Run(RefreshDevices);
            if (native.RecoveryRequired) status.Text = "Saved controller recovery is pending. Use Stop / restore before control.";
            if (Program.RemoteHost != null) { target.SelectedIndex = 1; host.Text = Program.RemoteHost; port.Value = Program.RemotePort; token.Text = Environment.GetEnvironmentVariable("PCUE_TOKEN") ?? ""; await Run(Connect); }
            timer.Start();
        };
        Closing += async (_, e) =>
        {
            if (allowClose || preview) return;
            e.Cancel = true; timer.Stop();
            await Run(async () =>
            {
                Check(local.StopHold());
                if (native.RecoveryRequired) await Task.Run(() => native.Restore(CoolingUsbDiscovery.Find()));
                native.Dispose(); remote.Dispose(); server?.Dispose(); local.Dispose(); instanceLock.Dispose(); packageLock?.Dispose();
                allowClose = true; Close();
            });
            if (!allowClose) timer.Start();
        };
    }
    static Grid Header()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,90,140,160,155") };
        string[] captions = { "Use", "Output", "RPM", "Mode / role", "Power % / RPM", "Min / Max / Avg" };
        for (int i = 0; i < captions.Length; i++) { var text = new TextBlock { Text = captions[i], FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(text, i); grid.Children.Add(text); }
        return grid;
    }
    Button Button(string caption, Func<Task> action)
    {
        var b = new Button { Content = caption, Padding = new Thickness(14, 9), MinHeight = 38 }; b.Click += async (_, _) => await Run(action); return b;
    }
    async Task Run(Func<Task> action)
    {
        if (preview) return;
        await operations.WaitAsync(); apply.IsEnabled = false; restore.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { operations.Release(); UpdateActions(); }
    }
    void UpdateActions()
    {
        apply.IsEnabled = !preview && (remoteActive ? remote.IsConnected : nativeActive ? native.Connected : local.IsConnected) && !local.Holding;
        restore.IsEnabled = !preview && !remoteActive && (native.Connected || native.RecoveryRequired || local.IsConnected);
    }
    async Task RefreshDevices()
    {
        if (nativeActive || local.IsConnected || remoteActive) throw new IOException("Disconnect before refreshing devices.");
        inventory = await Task.Run(CoolingUsbDiscovery.Find);
        devices.ItemsSource = new object[] { "Corsair Commander PRO (six outputs)" }.Concat(inventory.Cast<object>()).ToArray(); devices.SelectedIndex = 0;
    }
    async Task Connect()
    {
        if (remoteActive || nativeActive || local.IsConnected) throw new IOException("Disconnect the current target first.");
        if (native.RecoveryRequired) throw new IOException("Restore the saved controller before changing targets.");
        if (target.SelectedIndex == 1)
        {
            remoteSnapshot = await remote.ConnectAsync(host.Text, (int)(port.Value ?? 5056), token.Text); remoteActive = true; SetRows(6, false); InitializeProRows(remoteSnapshot); ShowSnapshot(remoteSnapshot); status.Text = "Connected to " + remote.Endpoint;
        }
        else if (devices.SelectedItem is CoolingUsbInfo device)
        {
            var state = await Task.Run(() => native.Connect(device)); nativeActive = true; SetRows(device.Outputs, true); ShowNative(state);
            for (int i = 0; i < rows.Count; i++) if (state.Powers != null && state.Powers[i] >= 0 && state.Powers[i] <= 100) rows[i].Value.Value = state.Powers[i];
            status.Text = "Connected to " + device.Name;
        }
        else { Check(await Task.Run(() => local.SetCommanderOpen(true))); SetRows(6, false); var snapshot = await Task.Run(local.GetStatus); InitializeProRows(snapshot); ShowSnapshot(snapshot); status.Text = "Connected to Commander PRO"; }
    }
    async Task Disconnect()
    {
        if (native.RecoveryRequired) throw new IOException("Use Stop / restore before disconnecting this controller.");
        if (remoteActive) { remote.Disconnect(); remoteActive = false; }
        else if (nativeActive) { native.Dispose(); nativeActive = false; }
        else Check(await Task.Run(() => local.SetCommanderOpen(false)));
        rows.ForEach(r => r.Rpm.Text = "—"); status.Text = "Disconnected. Commander PRO retains its last manual setting.";
    }
    void SetRows(int count, bool percentageOnly)
    {
        guidance.Text = percentageOnly ? "Select outputs explicitly. Use whole percentages 0–100. Mark pump outputs and keep them at 25% or higher." : "Select outputs explicitly. Commander PRO: 0–100 = power %, 101–10000 = fixed RPM on four-pin PWM fans.";
        ((TextBlock)header.Children[3]).Text = percentageOnly ? "Fan / pump" : "Fan mode";
        ((TextBlock)header.Children[4]).Text = percentageOnly ? "Power %" : "Power % / RPM";
        ((TextBlock)header.Children[5]).Text = percentageOnly ? "Controller setting" : "Min / Max / Avg";
        applyModes.IsEnabled = !percentageOnly; tachControls.IsEnabled = !percentageOnly;
        rows.Clear(); outputRows.Children.Clear();
        for (int i = 0; i < count; i++) { var row = new OutputRow(i, percentageOnly); rows.Add(row); outputRows.Children.Add(row.View); }
    }
    void InitializeProRows(PcueStatusSnapshot snapshot)
    {
        for (int i = 0; i < Math.Min(rows.Count, snapshot.fans.Count); i++)
        {
            rows[i].Value.Value = snapshot.fans[i].setpoint;
            rows[i].Role.SelectedIndex = snapshot.fans[i].mode switch { "3pin" => 1, "4pin" => 2, "disconnect" => 3, _ => 0 };
        }
    }
    async Task Apply()
    {
        var selected = rows.Select((r, i) => (r, i)).Where(x => x.r.Use.IsChecked == true).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Select at least one output.");
        foreach (var (r, _) in selected) if (r.Value.Value is not decimal n || n != decimal.Truncate(n)) throw new ArgumentException("Enter whole percentages or RPM values.");
        if (nativeActive)
        {
            int[] desired = rows.Select(r => r.Use.IsChecked == true ? (int)(r.Value.Value ?? -1) : -1).ToArray();
            bool[] pumps = rows.Select(r => r.Role.SelectedIndex == 1).ToArray();
            await Task.Run(() => native.Apply(desired, pumps));
        }
        else foreach (var (r, i) in selected)
        {
            int value = (int)r.Value.Value.Value;
            if (remoteActive) Check(await (value <= 100 ? remote.SetFanDutyAsync(i + 1, value) : remote.SetFanRpmAsync(i + 1, value)));
            else Check(await Task.Run(() => value <= 100 ? local.SetFanDuty(i + 1, value) : local.SetFanRpm(i + 1, value)));
        }
        status.Text = "Selected settings applied. Allow the fan or pump speed to settle.";
    }
    async Task ApplyModes()
    {
        if (nativeActive) throw new IOException("These controllers use percentage control and explicit fan/pump roles.");
        foreach (var (r, i) in rows.Select((r, i) => (r, i)).Where(x => x.r.Use.IsChecked == true))
        {
            string mode = new[] { "auto", "3pin", "4pin", "disconnect" }[r.Role.SelectedIndex];
            if (remoteActive) Check(await remote.SetFanModeAsync(i + 1, mode));
            else Check(await Task.Run(() => local.SetFanMode(i + 1, mode)));
        }
        status.Text = "Selected fan modes applied.";
    }
    async Task Restore()
    {
        if (remoteActive) throw new IOException("Remote pCUE restoration belongs to the remote application.");
        Check(local.StopHold());
        if (nativeActive || native.RecoveryRequired) { await Task.Run(() => native.Restore(CoolingUsbDiscovery.Find())); nativeActive = false; status.Text = "Saved controller settings restored and verified."; }
        else { Check(await Task.Run(() => local.SetCommanderOpen(false))); status.Text = "Commander disconnected; its last manual setting remains active."; }
    }
    async Task ConnectTach()
    {
        if (nativeActive) throw new IOException("RPM hold is available on Commander PRO only.");
        if (remoteActive) { Check(await remote.SetTachConnectedAsync(true)); Check(await remote.SetTachAssignmentAsync(holdFan.SelectedIndex + 1)); }
        else { Check(await Task.Run(() => local.SetTachConnected(true))); Check(local.SetTachAssignment(holdFan.SelectedIndex + 1)); }
        status.Text = "Tachometer connected and assigned to " + holdFan.SelectedItem;
    }
    async Task StartHold()
    {
        if (nativeActive) throw new IOException("RPM hold is available on Commander PRO only.");
        if (remoteActive) Check(await remote.StartHoldAsync(holdFan.SelectedIndex + 1, (int)(holdRpm.Value ?? 1000)));
        else { Check(local.SetTachAssignment(holdFan.SelectedIndex + 1)); Check(await Task.Run(() => local.StartHold(holdFan.SelectedIndex + 1, (int)(holdRpm.Value ?? 1000)))); }
    }
    async Task StopHold() { if (remoteActive) Check(await remote.StopHoldAsync()); else Check(local.StopHold()); }
    async Task Poll()
    {
        if (preview || !await operations.WaitAsync(0)) return;
        try
        {
            if (remoteActive) { if (remoteSnapshot != null) ShowSnapshot(remoteSnapshot); }
            else if (nativeActive) { var state = await Task.Run(native.Read); ShowNative(state); telemetry.Text = CpuText(new LinuxSensors().Read()); }
            else ShowSnapshot(await Task.Run(local.GetStatus));
            if (DateTime.UtcNow >= nextUpdateCheck)
            {
                nextUpdateCheck = DateTime.UtcNow.AddMinutes(30);
                pendingRelease = await LinuxAutoUpdate.AvailableAsync();
            }
            if (pendingRelease != null)
            {
                status.Text = "Update " + pendingRelease + " is queued for automatic installation when fan controls are idle.";
                if (!LinuxAutoUpdate.Managed && !remoteActive && !local.IsConnected && !local.Holding && !nativeActive && !native.RecoveryRequired)
                {
                    bool ready = await LinuxAutoUpdate.PreparePortableAsync(pendingRelease); pendingRelease = null;
                    if (ready && !remoteActive && !local.IsConnected && !local.Holding && !nativeActive && !native.RecoveryRequired) Dispatcher.UIThread.Post(Close);
                    else status.Text = "Automatic update deferred; the running application remains open.";
                }
            }
        }
        catch (Exception ex) { rows.ForEach(r => r.Rpm.Text = "Unavailable"); status.Text = ex.Message + (native.RecoveryRequired ? " Saved recovery remains pending." : ""); }
        finally { operations.Release(); UpdateActions(); }
    }
    void ShowSnapshot(PcueStatusSnapshot s)
    {
        if (s?.fans == null) return;
        for (int i = 0; i < Math.Min(rows.Count, s.fans.Count); i++)
        {
            var fan = s.fans[i]; rows[i].Rpm.Text = !remoteActive && !preview && local.RpmUnavailable[i] ? "Unavailable" : fan.rpm.ToString();
            rows[i].Stats.Text = $"{fan.min:0} / {fan.max:0} / {fan.average:0}";
        }
        telemetry.Text = CpuText(s.cpu);
        tachStatus.Text = $"Tachometer: {(s.tachometer?.connected == true ? s.tachometer.rpm?.ToString("0") + " RPM" : "disconnected")}   •   RPM hold: {s.hold?.status ?? "Idle"}";
    }
    static string CpuText(PcueCpuStatus s) => $"CPU temperature: {s?.temperature ?? "N/A"}   •   CPU frequency: {s?.mhz ?? "N/A"}   •   CPU load: {s?.load ?? "N/A"}";
    void ShowNative(CoreState s)
    {
        if (s == null) return;
        for (int i = 0; i < Math.Min(rows.Count, s.Connected.Length); i++)
        {
            var row = rows[i]; row.Rpm.Text = s.Rpm[i]?.ToString() ?? "—";
            row.Name.Text = s.OutputNames?.ElementAtOrDefault(i) ?? "Output " + (i + 1);
            row.Use.IsEnabled = s.Connected[i] && s.Writable;
            if (!row.Use.IsEnabled) row.Use.IsChecked = false;
            if (s.Connected.Length == 7 && i == 6) { row.Role.SelectedIndex = 1; row.Role.IsEnabled = false; row.Name.Text = "EXT pump"; }
            row.Stats.Text = s.Powers == null ? "Unavailable" : s.Powers[i] + (s.Modes?[i] == 1 ? " RPM stored" : "% stored");
        }
    }
    void ShowPreview()
    {
        status.Text = "Offline preview — illustrative readings; no USB devices opened.";
        for (int i = 0; i < rows.Count; i++) { rows[i].Use.IsChecked = i == 1; rows[i].Value.Value = 40 + i * 5; }
        ShowSnapshot(new PcueStatusSnapshot { fans = Enumerable.Range(1, 6).Select(i => new PcueFanStatus { fan = i, rpm = 800 + i * 140, min = 700, max = 1800, average = 1200 }).ToList(), cpu = new() { temperature = "48.2 °C", mhz = "4250 MHz", load = "12.5%" }, hold = new() { status = "Idle" }, tachometer = new() });
        UpdateActions();
    }
    public void SetPreviewMode(bool percentageOnly)
    {
        if (!preview) throw new InvalidOperationException("Preview data is isolated from live control.");
        SetRows(percentageOnly ? 14 : 6, percentageOnly);
        devices.ItemsSource = new[] { percentageOnly ? "Corsair iCUE LINK System Hub" : "Corsair Commander PRO (six outputs)" }; devices.SelectedIndex = 0;
        ShowPreview();
        if (percentageOnly)
        {
            ShowNative(new CoreState { Connected = Enumerable.Repeat(true, 14).ToArray(), Rpm = Enumerable.Range(0, 14).Select(i => (int?)(850 + i * 100)).ToArray(), Modes = new int[14], Powers = Enumerable.Repeat(80, 14).ToArray(), OutputNames = Enumerable.Range(1, 14).Select(i => i == 13 ? "Pump head" : "Chain output " + i).ToArray() });
            rows[12].Role.SelectedIndex = 1; rows[12].Value.Value = 100;
        }
    }
    public void ValidateLayout()
    {
        foreach (var row in rows) if (row.View.Bounds.Width < 760 || row.Value.Bounds.Width < 95 || row.Role.Bounds.Width < 105) throw new IOException("A critical output control was clipped.");
        if (apply.Bounds.Width < 160 || restore.Bounds.Width < 110) throw new IOException("A critical action caption was clipped.");
        Console.WriteLine($"UI layout passed: {Bounds.Width}x{Bounds.Height}, {rows.Count} output rows, {ActualThemeVariant.Key}.");
    }
    static void Check(string error) { if (!string.IsNullOrEmpty(error)) throw new IOException(error); }
    static void Check(PcueApiActionResponse response) { if (response?.ok != true) throw new IOException(response?.error ?? "Remote command was not acknowledged."); }
}
public sealed class OutputRow
{
    public Grid View { get; } = new() { ColumnDefinitions = new ColumnDefinitions("40,*,90,140,160,155"), MinHeight = 42 };
    public CheckBox Use { get; } = new();
    public TextBlock Name { get; } = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    public TextBlock Rpm { get; } = new() { Text = "—", VerticalAlignment = VerticalAlignment.Center };
    public TextBlock Stats { get; } = new() { Text = "—", VerticalAlignment = VerticalAlignment.Center };
    public ComboBox Role { get; } = new() { SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 12, 0) };
    public NumericUpDown Value { get; } = new() { Value = 50, Increment = 1, Minimum = 0, Margin = new Thickness(0, 0, 12, 0), FormatString = "0" };
    public OutputRow(int index, bool native)
    {
        Name.Text = "Fan " + (index + 1); Value.Maximum = native ? 100 : 10000;
        Role.ItemsSource = native ? new[] { "Fan", "Pump" } : new[] { "Auto", "3-pin / DC", "4-pin / PWM", "Disconnected" };
        Role.SelectionChanged += (_, _) => { if (native) Value.Minimum = Role.SelectedIndex == 1 ? 25 : 0; };
        int column = 0;
        foreach (Control control in new Control[] { Use, Name, Rpm, Role, Value, Stats }) { Grid.SetColumn(control, column++); View.Children.Add(control); }
    }
}
