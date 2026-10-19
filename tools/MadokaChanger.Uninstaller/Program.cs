using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MadokaChanger.Uninstaller;

internal static class Program
{
    // Exit codes: 0 = done (or nothing found), 1 = something could not be removed, 2 = cancelled.
    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(params string[] names) => args.Any(a => names.Any(n => string.Equals(a.TrimStart('/', '-'), n, StringComparison.OrdinalIgnoreCase)));
        var list = Has("list");
        var silent = Has("silent", "quiet", "verysilent");
        if (Has("?", "h", "help"))
        {
            AttachOutput();
            Console.WriteLine("MadokaChanger-Uninstaller [/list | /silent [/deletedata]]");
            Console.WriteLine("  (none)       Show the window.");
            Console.WriteLine("  /list        Print what would be removed. Deletes nothing.");
            Console.WriteLine("  /silent      Remove old MadokaChanger copies with no window. Keeps skins and settings.");
            Console.WriteLine("  /deletedata  With /silent, also delete skins and settings.");
            return 0;
        }

        if (list || silent)
        {
            AttachOutput();
            var scan = Scanner.Scan();
            Console.WriteLine(scan.Items.Count == 0 ? "No MadokaChanger copies found." : $"Found {scan.Items.Count} item(s):");
            foreach (var item in scan.Items) Console.WriteLine("  " + item + (item.Kind == ItemKind.DataFolder && !(silent && Has("deletedata")) ? " (kept)" : ""));
            if (list) { Console.WriteLine("List only: nothing was deleted."); return 0; }

            var report = Remover.Run(scan, deleteData: Has("deletedata"), progress: s => Console.WriteLine(s));
            foreach (var line in report.Lines) Console.WriteLine(line);
            Console.WriteLine($"Done: {report.Removed} removed, {report.Failed} failed.");
            return report.Failed == 0 ? 0 : 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new MainForm();
        Application.Run(form);
        return form.ExitCode;
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);

    // A WinExe has no console. When output is redirected it already works; otherwise borrow the parent's console.
    private static void AttachOutput()
    {
        var stdout = GetStdHandle(-11);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1))
        {
            if (AttachConsole(-1))
            {
                var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(writer);
                Console.WriteLine();
            }
        }
    }
}
