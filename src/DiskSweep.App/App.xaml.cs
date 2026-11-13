/*
 * DiskSweep
 * Copyright (c) 2026 Miloš Milinkov. All rights reserved.
 * Proprietary and confidential. Use, copying, modification or distribution
 * without written permission is prohibited. See the LICENSE file.
 */

using System.Windows;
using System.Windows.Threading;
using DiskSweep.Core.Logging;

namespace DiskSweep.App;

public partial class App : Application
{
    public static ActionLog Log { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log = new ActionLog();
        Log.Info($"DiskSweep {typeof(App).Assembly.GetName().Version} started.");
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unexpected error in the window", e.Exception);
        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nNothing on your disk was changed. Details are in the log:\n{Log.FilePath}",
            "DiskSweep", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("DiskSweep closed.");
        Log.Dispose();
        base.OnExit(e);
    }
}
