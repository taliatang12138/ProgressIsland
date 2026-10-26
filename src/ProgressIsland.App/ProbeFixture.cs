using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ProgressIsland.App;

// Controlled local fixture used to verify a real cross-process accessibility read.
internal sealed class ProbeFixture : Window
{
    public ProbeFixture(string value)
    {
        Title = "Progress Island verification fixture"; Width = 420; Height = 180;
        var progress = new ProgressBar { Minimum = 0, Maximum = 200, Value = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), Height = 16, Margin = new Thickness(25) };
        AutomationProperties.SetName(progress, "测试任务进度"); Content = progress;
        Closed += (_, _) => Application.Current.Shutdown();
    }
}
