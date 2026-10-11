using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Drive Folder Sync")]
[assembly: AssemblyProduct("Drive Folder Sync")]
[assembly: AssemblyVersion("1.5.1.0")]
[assembly: AssemblyFileVersion("1.5.1.0")]

namespace DriveFolderSync
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Store.EnsureDirs();
                if (Has(args, "/selftest"))
                {
                    string result = ValueAfter(args, "/selftest");
                    if (string.IsNullOrEmpty(result) || result.StartsWith("/"))
                        result = Path.Combine(Path.GetTempPath(), "drivesync-selftest.txt");
                    Environment.Exit(SelfTest.Run(result));
                }
                if (Has(args, "/uitest"))
                {
                    string output = ValueAfter(args, "/uitest");
                    if (string.IsNullOrEmpty(output)) output = Path.Combine(Path.GetTempPath(), "drivesync-ui-test");
                    Environment.Exit(UiSelfTest.Run(output));
                }
                if (Has(args, "/uninstall"))
                {
                    Installer.Uninstall(Has(args, "/quiet"));
                    return;
                }
                if (Has(args, "/host"))
                {
                    HostForm.Run();
                    return;
                }
                if (Has(args, "/install") || !Store.IsInstalledCopy())
                {
                    if (Has(args, "/quiet"))
                    {
                        Installer.Install(true);
                        return;
                    }
                    Application.Run(new SetupForm());
                    return;
                }
                RunUi(ShellPath(ValueAfter(args, "/link")));
            }
            catch (Exception ex)
            {
                try { Store.Log("fatal: " + ex); }
                catch { }
                if (!Has(args, "/update")) MessageBox.Show(ex.Message, "Drive Folder Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void RunUi(string linkPath)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\DriveFolderSyncUi", out created))
            {
                EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DriveFolderSyncUiShow");
                if (!created)
                {
                    // Hand a right-clicked folder to the window that is already open.
                    if (!string.IsNullOrEmpty(linkPath))
                    {
                        try { File.WriteAllText(LinkRequestPath, linkPath); }
                        catch (Exception ex) { Store.Log("could not pass the folder to the open window: " + ex.Message); }
                    }
                    show.Set();
                    return;
                }
                MainForm form = new MainForm(linkPath);
                Thread thread = new Thread(delegate()
                {
                    while (show.WaitOne())
                    {
                        try
                        {
                            form.BeginInvoke(new Action(delegate
                            {
                                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                                form.Show();
                                form.Activate();
                                string requested = null;
                                try
                                {
                                    if (File.Exists(LinkRequestPath))
                                    {
                                        requested = File.ReadAllText(LinkRequestPath).Trim();
                                        File.Delete(LinkRequestPath);
                                    }
                                }
                                catch (Exception ex) { Store.Log("could not read the folder request: " + ex.Message); }
                                if (!string.IsNullOrEmpty(requested)) form.RequestAttach(requested);
                            }));
                        }
                        catch { }
                    }
                });
                thread.IsBackground = true;
                thread.Start();
                Application.Run(form);
            }
        }

        static string LinkRequestPath
        {
            get { return Path.Combine(Store.Root, "link-request.txt"); }
        }

        // Explorer passes a drive root as "D:\" which arrives as D:" after argument parsing.
        static string ShellPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Trim().TrimEnd('"');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            return path;
        }

        static bool Has(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string ValueAfter(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
