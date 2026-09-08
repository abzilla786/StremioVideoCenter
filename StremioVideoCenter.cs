using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.SDK.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;


namespace StremioVideoCenter
{
    public class StremioVideoCenter : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private System.Windows.Window videoCenterWindow;
        private System.Windows.Window playniteWindow;
        private StremioVideoCenterSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");

        public StremioVideoCenter(IPlayniteAPI api) : base(api)
        {
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                ElementList = new List<string>
    {
        "VideoCenter"
    },
                SourceName = "StremioVideoCenter"
            });

            settings = new StremioVideoCenterSettingsViewModel(this);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(
            GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Stremio Video Center",
                MenuSection = "@",
                Action = _ =>
                {
                    OpenVideoCenter();
                }
            };
        }

        private void OpenVideoCenter()
        {
            // Don't open multiple Video Center windows.
            if (videoCenterWindow != null)
            {
                if (videoCenterWindow.WindowState == System.Windows.WindowState.Minimized)
                {
                    videoCenterWindow.WindowState =
                        System.Windows.WindowState.Normal;
                }

                videoCenterWindow.Activate();
                videoCenterWindow.Focus();

                return;
            }

            // Store the Playnite window so we can reliably
            // return focus to it when Video Center closes.
            playniteWindow = PlayniteApi.Dialogs.GetCurrentAppWindow();

            if (playniteWindow == null)
            {
                logger.Warn("Unable to obtain the Playnite application window.");
                return;
            }

            videoCenterWindow = PlayniteApi.Dialogs.CreateWindow(
                new WindowCreationOptions
                {
                    ShowCloseButton = false,
                    ShowMaximizeButton = false,
                    ShowMinimizeButton = false
                });

            videoCenterWindow.Title = "Stremio Video Center";

            try
            {
                videoCenterWindow.Content = new StremioVideoCenterView(
                                                settings?.Settings?.TmdbApiKey);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to create StremioVideoCenterView.");

                System.Windows.MessageBox.Show(
                    ex.ToString(),
                    "Video Center Load Error");

                videoCenterWindow.Close();
                videoCenterWindow = null;

                return;
            }

            videoCenterWindow.WindowStyle =
                System.Windows.WindowStyle.None;

            videoCenterWindow.ResizeMode =
                System.Windows.ResizeMode.NoResize;

            videoCenterWindow.ShowInTaskbar = false;

            videoCenterWindow.WindowState =
                System.Windows.WindowState.Normal;

            // Make Playnite the owner of the Video Center window.
            // This helps Windows restore focus to Playnite when
            // the Video Center closes.
            videoCenterWindow.Owner = playniteWindow;

            // Match the Playnite window exactly.
            videoCenterWindow.Left = playniteWindow.Left;
            videoCenterWindow.Top = playniteWindow.Top;
            videoCenterWindow.Width = playniteWindow.ActualWidth;
            videoCenterWindow.Height = playniteWindow.ActualHeight;

            // Keyboard close handling.
            videoCenterWindow.KeyDown += VideoCenterWindow_KeyDown;

            // Clean up our reference when the window closes.
            videoCenterWindow.Closed += VideoCenterWindow_Closed;

            videoCenterWindow.Show();
            videoCenterWindow.Activate();
            videoCenterWindow.Focus();
        }

        private void VideoCenterWindow_KeyDown(
            object sender,
            System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape ||
                e.Key == System.Windows.Input.Key.Back)
            {
                CloseVideoCenter();

                e.Handled = true;
            }
        }

        public override System.Windows.Controls.Control GetGameViewControl(
            GetGameViewControlArgs args)
        {
            if (args.Name == "VideoCenter")
            {
                return new StremioVideoCenterView(
                           settings?.Settings?.TmdbApiKey);
            }

            return null;
        }

        public override void OnControllerButtonStateChanged(
    OnControllerButtonStateChangedArgs args)
        {
            if (videoCenterWindow == null)
            {
                return;
            }

            if (args.Button == ControllerInput.B &&
                args.State == ControllerInputState.Pressed)
            {
                CloseVideoCenter();
            }
        }

        private void VideoCenterWindow_Closed(
     object sender,
     EventArgs e)
        {
            if (videoCenterWindow != null)
            {
                videoCenterWindow.KeyDown -= VideoCenterWindow_KeyDown;
                videoCenterWindow.Closed -= VideoCenterWindow_Closed;
            }

            videoCenterWindow = null;

            // Restore focus to Playnite after the Video Center
            // window has completely closed.
            if (playniteWindow != null)
            {
                playniteWindow.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        try
                        {
                            if (playniteWindow.WindowState ==
                                System.Windows.WindowState.Minimized)
                            {
                                playniteWindow.WindowState =
                                    System.Windows.WindowState.Normal;
                            }

                            playniteWindow.Activate();
                            playniteWindow.Focus();

                            System.Windows.Input.Keyboard.Focus(playniteWindow);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Failed to restore focus to Playnite.");
                        }
                    }));
            }
        }

        private void CloseVideoCenter()
        {
            if (videoCenterWindow == null)
            {
                return;
            }

            videoCenterWindow.Close();
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            // Add code to be executed when game is finished installing.
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            // Add code to be executed when game is started running.
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            // Add code to be executed when game is uninstalled.
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Add code to be executed when Playnite is initialized.
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            // Add code to be executed when Playnite is shutting down.
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // Add code to be executed when library is updated.
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new StremioVideoCenterSettingsView();
        }
    }
}