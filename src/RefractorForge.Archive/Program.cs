namespace RefractorForge.Archive;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Opening straight from an argument means the .rfa file association and "open with" both work.
        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }
}
