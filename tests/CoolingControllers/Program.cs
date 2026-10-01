using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pCUE;

static class Program
{
    static void Check(bool pass, string why) { if (!pass) throw new Exception(why); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            CoreBackendTests.Run();
            BeQuietBackendTests.Run();
            var row = new CoolingOutputRow { Minimum = 30, Percent = 40 }; row.Role = "Pump";
            Check(row.Minimum == 60 && row.Percent == 100 && !row.Enabled, "Pump defaults/opt-in.");
            row.Percent = 75; row.Role = "Pump"; Check(row.Percent == 75, "Existing pump percentage reset.");
            var ioPair = new[] { new CoolingOutputRow { Channel = 0, Role = "Pump", Minimum = 25, Percent = 100, Enabled = true }, new CoolingOutputRow { Channel = 1, Role = "Fan", Enabled = true } };
            Check(CoolingControllersWindow.ValidIoPair(ioPair), "Commissioned IO pair rejected."); ioPair[1].Enabled = false; Check(!CoolingControllersWindow.ValidIoPair(ioPair), "Fan-only IO claim accepted."); ioPair[1].Enabled = true; ioPair[0].Minimum = 20; Check(!CoolingControllersWindow.ValidIoPair(ioPair), "IO pump below commissioned range accepted.");
            var invalid = new TextBox(); invalid.SetBinding(TextBox.TextProperty, new Binding("Percent") { Source = row, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
            invalid.Text = "not a percentage"; invalid.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            Check(Validation.GetHasError(invalid), "Invalid percentage was silently accepted.");
            Check((bool)typeof(CoolingControllersWindow).GetMethod("HasInputError", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { invalid }), "Grid input-error gate.");
            string folder = args.Length == 1 ? args[0] : null;
            foreach (int width in new[] { 1000, 850 })
            {
                var window = new CoolingControllersWindow(true); window.Width = width;
                var root = (FrameworkElement)window.Content;
                int height = width == 1000 ? 673 : 533;
                root.Measure(new Size(width - 16, height)); root.Arrange(new Rect(0, 0, width - 16, height)); root.UpdateLayout();
                var layout = (Grid)((Border)root).Child; var grid = layout.Children.OfType<DataGrid>().Single();
                Check(!window.IsVisible && !window.PendingRecovery && !window.Busy, "Preview touched session/recovery or displayed a window.");
                Check(grid.Items.Count == 7 && grid.ActualHeight >= 180 && grid.Columns.Sum(c => c.ActualWidth) + 20 < grid.ActualWidth, "Output inventory clipped or empty: items=" + grid.Items.Count + ", height=" + grid.ActualHeight + ", columns=" + grid.Columns.Sum(c => c.ActualWidth) + ", viewport=" + grid.ActualWidth);
                if (width == 1000) Check(grid.ActualHeight >= 7 * grid.MinRowHeight + 28, "Default window does not fit seven outputs.");
                foreach (var actions in layout.Children.OfType<StackPanel>()) foreach (var button in actions.Children.OfType<Button>()) Check(button.ActualWidth > 70 && button.ActualHeight > 30 && !button.IsEnabled, "Preview action size/isolation.");
                if (folder != null)
                {
                    Directory.CreateDirectory(folder);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(folder, "CORE-panel-" + width + ".png"))) encoder.Save(file);
                }
                window.Close();
            }
            Console.WriteLine("PASS: CORE panel preview, narrow layout, pump defaults and invalid percentage gates (96 DPI; illustrative)."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
