using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace pCUE
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Single instance: two pCUEs fight over the same Commander HID device (the second gets
        // "Cannot open Commander Pro!"). The first instance wins; later ones exit silently.
        private static System.Threading.Mutex _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool created;
            try
            {
                _singleInstance = new System.Threading.Mutex(true,
                    @"Global\pCUE_Cybenetics_SingleInstance", out created);
            }
            catch
            {
                created = true;
            }
            if (!created)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    AppLog.Error("Unhandled UI exception: " + args.Exception);
                    args.Handled = true;
                    MessageBox.Show("pCUE hit an unexpected error and logged it. Fans keep their current setting.",
                        "pCUE", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { args.Handled = false; }
            };

            // Settings migration guard. AssemblyVersion is pinned at 1.1.0.0 precisely so the
            // settings store never versions out from under the user, which makes this a no-op
            // today - but if the pin is ever lifted, saved settings migrate instead of resetting
            // to defaults silently. Never throws: a settings failure must not kill startup.
            try { pCUE.Properties.Settings.Default.Upgrade(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("pCUE: settings upgrade failed: " + ex.Message);
            }

            base.OnStartup(e);
        }
    }
}
