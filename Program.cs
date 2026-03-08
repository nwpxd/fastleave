namespace FastLeave;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Global\FastLeave", out bool created);
        if (!created)
        {
            MessageBox.Show("FastLeave is already running.", "FastLeave",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
