using Rigsight.Core.Boot;

namespace Rigsight.Boot;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["--enable", var sid]: Setup.Set(true, sid); break;
                case ["--disable"]: Setup.Set(false); break;
                case ["--pause-worker"]: Setup.PauseForUpdate(true); break;
                case ["--resume-worker"]: Setup.PauseForUpdate(false); break;
                case ["--worker"]: Worker.Run(); break;
                case ["--parse", var directory]: BootParser.Parse(directory); break;
                default: return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            BootStore.Log(ex.ToString());
            Rigsight.Core.Log.Error("boot", ex);
            // Setup can fail before the protected storage exists: show the explanation to the caller as well.
            if (args.FirstOrDefault() is "--enable" or "--disable")
                MessageBox(IntPtr.Zero, ex.Message, "Rigsight boot recording", 0x10);
            return 1;
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string title, uint type);
}
