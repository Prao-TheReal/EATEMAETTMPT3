using System.Runtime.InteropServices;

namespace Remnant2ESP;

static class Program
{
    // [FIX] Import DPI Awareness function
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        // [FIX] Call this BEFORE creating any forms
        SetProcessDPIAware();

        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm());
    }
}