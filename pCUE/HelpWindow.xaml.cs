using System.Windows;

namespace pCUE
{
    /// <summary>
    /// Explains what every control does, in plain language. Reached from the "?" button.
    ///
    /// It leans on the two traps that actually cost bench time: the fan-mode drop-down must match
    /// the fan (a PWM fan set to 3-pin will not spin, and Auto can mis-detect), and a fixed RPM
    /// target only works when the Commander can read that fan's speed-sense wire.
    /// </summary>
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
