using Microsoft.Gaming.XboxGameBar;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RotationTracker
{
    sealed partial class App : Application
    {
        private const string LogFileName = "rotationtracker.log";
        private XboxGameBarWidget _widget;

        /// <summary>
        /// Accessible by pages that need the active widget reference after
        /// navigation (for example, going back from the settings page).
        /// </summary>
        public static XboxGameBarWidget CurrentWidget { get; private set; }

        public App()
        {
            BootstrapLog("App ctor entered.");

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                BootstrapLog("UnobservedTaskException", e.Exception);
                try { e.SetObserved(); } catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                BootstrapLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

            try
            {
                InitializeComponent();
                BootstrapLog("InitializeComponent ok.");
            }
            catch (Exception ex)
            {
                BootstrapLog("InitializeComponent FAILED", ex);
                throw;
            }

            Suspending += OnSuspending;
            UnhandledException += (_, e) =>
                BootstrapLog("UI UnhandledException: " + e.Message, e.Exception);
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            try
            {
                BootstrapLog($"OnActivated kind={args?.Kind}");

                if (args.Kind != ActivationKind.Protocol)
                {
                    BootstrapLog("Non-protocol activation; falling through to base.");
                    base.OnActivated(args);
                    return;
                }

                // Widget activation through the microsoft.gameBarUIExtension ActivationUri.
                var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                if (widgetArgs != null)
                {
                    ActivateAsWidget(widgetArgs);
                    return;
                }

                BootstrapLog("Activation was protocol but not Game Bar widget.");
                base.OnActivated(args);
            }
            catch (Exception ex)
            {
                BootstrapLog("OnActivated FAILED", ex);
                throw;
            }
        }

        private void ActivateAsWidget(XboxGameBarWidgetActivatedEventArgs widgetArgs)
        {
            BootstrapLog($"Widget activation. IsLaunchActivation={widgetArgs.IsLaunchActivation}");

            if (!widgetArgs.IsLaunchActivation)
            {
                BootstrapLog("Widget reactivation ignored.");
                return;
            }

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            Window.Current.Content = rootFrame;

            _widget = new XboxGameBarWidget(
                widgetArgs,
                Window.Current.CoreWindow,
                rootFrame);
            CurrentWidget = _widget;
            BootstrapLog("XboxGameBarWidget created.");

            rootFrame.Navigate(typeof(WidgetPage), _widget);
            BootstrapLog("Navigated to WidgetPage.");

            Window.Current.Closed += WidgetWindow_Closed;
            Window.Current.Activate();
            BootstrapLog("Window activated.");
        }

        private void WidgetWindow_Closed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            BootstrapLog("Widget window closed.");
            _widget = null;
            CurrentWidget = null;
            Window.Current.Closed -= WidgetWindow_Closed;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                BootstrapLog($"OnLaunched args={e?.Arguments}");
                Frame rootFrame = Window.Current.Content as Frame;

                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootFrame;
                }

                if (!e.PrelaunchActivated)
                {
                    if (rootFrame.Content == null)
                    {
                        rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    }
                    Window.Current.Activate();
                }
            }
            catch (Exception ex)
            {
                BootstrapLog("OnLaunched FAILED", ex);
                throw;
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            BootstrapLog($"Navigation failed to {e.SourcePageType.FullName}", e.Exception);
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            BootstrapLog("Suspending.");
            _widget = null;
            deferral.Complete();
        }

        /// <summary>
        /// Best-effort file logger used both for routine tracing and for
        /// diagnosing crashes. Writes to the app's LocalFolder, falling
        /// back to %TEMP% when LocalFolder is not reachable yet.
        /// </summary>
        internal static void BootstrapLog(string message, Exception ex = null)
        {
            try
            {
                string logPath = null;
                try
                {
                    logPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, LogFileName);
                }
                catch
                {
                    // ApplicationData.Current may not be ready during early activation failures.
                }

                if (string.IsNullOrEmpty(logPath))
                {
                    logPath = Path.Combine(Path.GetTempPath(), "rotationtracker-fallback.log");
                }

                var line = $"{DateTimeOffset.Now:O} | {message}";
                if (ex != null)
                {
                    line += $" | {ex.GetType().FullName}: {ex.Message}";
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        line += Environment.NewLine + ex.StackTrace;
                    }
                }
                line += Environment.NewLine;

                File.AppendAllText(logPath, line);
                Trace.WriteLine(message);
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
