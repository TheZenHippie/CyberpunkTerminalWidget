using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CyberpunkTerminalWidget
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static int _isExiting = 0;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Force submenus to fly out to the right away from the checkmark/icon gutter
            EnsureStandardMenuDropAlignment();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, args) => EnsureStandardMenuDropAlignment();

            // Handle unhandled non-UI exceptions safely
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    if (args.ExceptionObject is Exception ex)
                    {
                        MessageBox.Show($"An unexpected error occurred: {ex.Message}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                finally
                {
                    CleanProcessExit(1);
                }
            };

            // Handle unhandled UI dispatcher exceptions safely
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    var ex = args.Exception;
                    while (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                    {
                        ex = tie.InnerException;
                    }

                    string details = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
                    MessageBox.Show($"An unexpected UI error occurred:\n{details}", "Application Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    args.Handled = true;
                }
                catch
                {
                    CleanProcessExit(1);
                }
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            CleanProcessExit(e.ApplicationExitCode);
        }

        /// <summary>
        /// Guarantees a zero-footprint, immediate clean exit with no lingering background processes,
        /// shell sub-processes, or worker threads remaining in Windows Task Manager.
        /// </summary>
        public static void CleanProcessExit(int exitCode = 0)
        {
            if (System.Threading.Interlocked.Exchange(ref _isExiting, 1) != 0)
            {
                return;
            }

            try
            {
                // Terminate any active terminal sessions
                Terminal.TerminalSession.KillAllActiveSessions();

                // Force full generation 2 garbage collection and drain all pending finalizers
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true);
            }
            catch
            {
                // Ignore any teardown exceptions during final collection
            }
            finally
            {
                // Guarantees immediate termination of the process and all native worker threads
                Environment.Exit(exitCode);
            }
        }

        /// <summary>
        /// Fixes the Windows / WPF MenuDropAlignment bug where submenus fly out to the left over
        /// the checkmark/icon gutter instead of to the right.
        /// </summary>
        public static void EnsureStandardMenuDropAlignment()
        {
            if (SystemParameters.MenuDropAlignment)
            {
                try
                {
                    var field = typeof(SystemParameters).GetField("_menuDropAlignment",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (field != null)
                    {
                        field.SetValue(null, false);
                    }
                }
                catch { }
            }
        }
    }
}

