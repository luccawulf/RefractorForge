namespace RefractorForge.Archive;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // A file tool that vanishes without a word is the worst outcome for someone mid-edit. Anything unhandled
        // is written beside the executable and shown, and the window stays up so unsaved work can still be saved.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        // Opening straight from an argument means the .rfa file association and "open with" both work; a folder
        // opens as a mod.
        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }

    private static void Report(Exception ex)
    {
        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{ex}\r\n";
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "archive-crash.log"), text + "\r\n"); } catch { }
        try
        {
            MessageBox.Show(
                $"Something went wrong:\r\n\r\n{ex.GetType().Name}: {ex.Message}\r\n\r\n" +
                "Details were written to archive-crash.log next to the program. The window will stay open so you can save.",
                "RefractorForge Archive", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
