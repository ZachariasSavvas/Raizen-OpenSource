namespace Raizen.Server.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance guard
        using var mutex = new System.Threading.Mutex(true, "RaizenServerSetup_SingleInstance",
            out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Raizen Server Setup is already running.",
                "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // Surface unhandled exceptions from UI thread events instead of crashing silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Unexpected Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        try
        {
            Application.Run(new WizardForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Startup Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
