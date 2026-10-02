using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.SDK.Controls;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
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
        private StremioVideoCenterView videoCenterView;
        private StremioVideoCenterSettingsViewModel settings { get; set; }

        // Playnite's on-screen bounds captured in true physical device
        // pixels (via PointToScreen) at the moment Video Center is opened.
        // Physical pixels are DPI-context-independent, unlike
        // Left/Top/ActualWidth/ActualHeight, which are device-independent
        // units relative to *that specific window's own* per-monitor DPI
        // context. See VideoCenterWindow_Loaded for why this matters.
        private System.Windows.Point playniteTopLeftPhysical;
        private System.Windows.Point playniteBottomRightPhysical;

        public override Guid Id { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");

        public StremioVideoCenter(IPlayniteAPI api) : base(api)
        {
            // One stable, appended-to application log for StremioVideoCenter-
            // side diagnostics, stored in this extension's own Playnite
            // user-data folder. Playnite-integration diagnostics continue to
            // go through the Playnite `logger` above instead - see
            // StremioVideoCenterLogger.cs.
            try
            {
                StremioVideoCenterLogger.Initialize(GetPluginUserDataPath());
                StremioVideoCenterLogger.Info("StremioVideoCenter plugin initialized.");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to initialize StremioVideoCenterLogger.");
            }

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

                EnsureVideoCenterForeground();

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
                ProfileService profileService = CreateProfileService();

                var configuration = new StremioVideoCenterConfiguration
                {
                    OmdbApiKey = settings?.Settings?.OmdbApiKey,
                    RealDebridApiKey = settings?.Settings?.RealDebridApiKey,
                    StremioAddonUrls = settings?.Settings?.StremioAddonUrls,
                    AddonInstances = settings?.Settings?.AddonInstances,
                    LibraryLocations = settings?.Settings?.LibraryLocations,
                    ProfileService = profileService,
                    WatchStateService = CreateWatchStateService(profileService),
                    WatchHistoryService = CreateWatchHistoryService(profileService),
                    MyListService = CreateMyListService(profileService)
                };

                videoCenterView = new StremioVideoCenterView(configuration);

                videoCenterWindow.Content = videoCenterView;
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

            videoCenterWindow.SizeToContent =
                System.Windows.SizeToContent.Manual;

            videoCenterWindow.WindowStartupLocation =
                System.Windows.WindowStartupLocation.Manual;

            // Make Playnite the owner of the Video Center window.
            // This helps Windows restore focus to Playnite when
            // the Video Center closes.
            videoCenterWindow.Owner = playniteWindow;

            // Capture Playnite's on-screen bounds in true physical device
            // pixels now, while playniteWindow's own DPI context is what's
            // in effect. PointToScreen is DPI-context-independent, unlike
            // Left/Top/ActualWidth/ActualHeight (device-independent units
            // relative to that window's own per-monitor DPI context). On a
            // multi-monitor setup where the monitors don't share the same
            // DPI scale, copying Left/Top/ActualWidth/ActualHeight directly
            // onto a different window renders it at the wrong physical
            // size/position - this is what caused the Video Center window
            // to render wider than Playnite's own window and spill onto an
            // adjacent monitor instead of covering only the Playnite
            // window. Converting through physical pixels (finished in
            // VideoCenterWindow_Loaded, once videoCenterWindow has its own
            // DPI context to convert back into) keeps the two windows
            // aligned pixel-for-pixel regardless of DPI differences.
            playniteTopLeftPhysical = playniteWindow.PointToScreen(
                new System.Windows.Point(0, 0));

            playniteBottomRightPhysical = playniteWindow.PointToScreen(
                new System.Windows.Point(
                    playniteWindow.ActualWidth,
                    playniteWindow.ActualHeight));

            // Best-effort initial placement using the raw DIU values, in
            // case this window's DPI context happens to match Playnite's
            // (the common single-monitor/uniform-DPI case). This also
            // avoids the window flashing at its default dialog size before
            // Loaded fires. VideoCenterWindow_Loaded performs the
            // authoritative, DPI-correct placement once it can.
            videoCenterWindow.Left = playniteWindow.Left;
            videoCenterWindow.Top = playniteWindow.Top;
            videoCenterWindow.Width = playniteWindow.ActualWidth;
            videoCenterWindow.Height = playniteWindow.ActualHeight;

            // Authoritative placement, performed once the window has a
            // PresentationSource (and therefore its own DPI context) to
            // convert the physical-pixel bounds captured above back into.
            videoCenterWindow.Loaded += VideoCenterWindow_Loaded;

            // Keyboard close handling.
            videoCenterWindow.KeyDown += VideoCenterWindow_KeyDown;

            // Clean up our reference when the window closes.
            videoCenterWindow.Closed += VideoCenterWindow_Closed;

            videoCenterWindow.Show();

            EnsureVideoCenterForeground();
        }

        // =========================================================
        // FOREGROUND ACTIVATION (window activation/input fix)
        // =========================================================
        //
        // videoCenterWindow.Show()/Activate()/Focus() alone were not
        // reliably winning real OS input focus away from Playnite's own
        // window - manual testing showed Zino rendered on screen (owned
        // windows always draw above their owner) but received no mouse
        // hover, no keyboard, and no D-pad/A input, while Playnite's own
        // OnControllerButtonStateChanged callback (which never goes
        // through window focus at all) still worked, proving this was a
        // focus/activation gap rather than a dead window.
        //
        // Two things are happening here, deliberately kept out of the
        // main flow rather than scattered inline:
        //   1. Activate()/Focus() are re-asserted, not just called once
        //      immediately after Show() (before the window has a
        //      PresentationSource/is fully realized - the same timing gap
        //      VideoCenterWindow_Loaded already exists to work around for
        //      DPI-correct bounds). This method is also called again from
        //      VideoCenterWindow_Loaded, once the window is fully shown.
        //   2. A transient Topmost toggle (true, then immediately back to
        //      false) is a well-known, dependency-free way to make
        //      Windows re-evaluate this window's z-order/activation. This
        //      is NOT a permanent Topmost - the window is never left
        //      pinned above everything else, so it cannot interfere with
        //      other applications or outlive Zino being open.
        //
        // No P/Invoke, global hooks, or mouse/keyboard capture are
        // introduced - this project has none of those today (verified via
        // a full-project search before making this change) and none are
        // needed: both mechanisms used here are ordinary managed
        // System.Windows.Window members already available on
        // videoCenterWindow.
        private void EnsureVideoCenterForeground()
        {
            if (videoCenterWindow == null)
            {
                return;
            }

            videoCenterWindow.Activate();
            videoCenterWindow.Focus();

            videoCenterWindow.Topmost = true;
            videoCenterWindow.Topmost = false;
        }

        private void VideoCenterWindow_Loaded(
            object sender,
            System.Windows.RoutedEventArgs e)
        {
            if (videoCenterWindow == null)
            {
                return;
            }

            videoCenterWindow.Loaded -= VideoCenterWindow_Loaded;

            var source = System.Windows.PresentationSource.FromVisual(videoCenterWindow);

            if (source == null || source.CompositionTarget == null)
            {
                // No DPI context available to convert through - leave the
                // best-effort raw DIU placement already applied in
                // OpenVideoCenter() in place rather than risk an
                // uninitialized transform.
                logger.Warn(
                    "Video Center window has no PresentationSource in Loaded; " +
                    "keeping best-effort window bounds.");
            }
            else
            {
                var transformFromDevice = source.CompositionTarget.TransformFromDevice;

                var topLeftLocal = transformFromDevice.Transform(playniteTopLeftPhysical);
                var bottomRightLocal = transformFromDevice.Transform(playniteBottomRightPhysical);

                videoCenterWindow.Left = topLeftLocal.X;
                videoCenterWindow.Top = topLeftLocal.Y;
                videoCenterWindow.Width = bottomRightLocal.X - topLeftLocal.X;
                videoCenterWindow.Height = bottomRightLocal.Y - topLeftLocal.Y;
            }

            // Re-assert foreground activation now that the window is
            // fully realized and correctly sized/positioned - see
            // EnsureVideoCenterForeground's own remarks for why the
            // earlier call from OpenVideoCenter() alone was not reliable.
            EnsureVideoCenterForeground();
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
                ProfileService profileService = CreateProfileService();

                var configuration = new StremioVideoCenterConfiguration
                {
                    OmdbApiKey = settings?.Settings?.OmdbApiKey,
                    RealDebridApiKey = settings?.Settings?.RealDebridApiKey,
                    StremioAddonUrls = settings?.Settings?.StremioAddonUrls,
                    AddonInstances = settings?.Settings?.AddonInstances,
                    LibraryLocations = settings?.Settings?.LibraryLocations,
                    ProfileService = profileService,
                    WatchStateService = CreateWatchStateService(profileService),
                    WatchHistoryService = CreateWatchHistoryService(profileService),
                    MyListService = CreateMyListService(profileService)
                };

                return new StremioVideoCenterView(configuration);
            }

            return null;
        }

        // =========================================================
        // PROFILE SERVICE CONSTRUCTION (Slice 16A - Profile Foundation)
        // =========================================================
        //
        // One ProfileService per Video Center window-open, built here -
        // where the live StremioVideoCenterSettings instance and this
        // plugin's own SavePluginSettings are both naturally available -
        // rather than in the View, which only ever receives read-only
        // configuration snapshots for everything else (see
        // StremioVideoCenterConfiguration.ProfileService remarks). The
        // save delegate calls this plugin's own SavePluginSettings with
        // the SAME live Settings object ProfileService mutates, so a
        // profile create/rename/switch made from inside the open window
        // is written to Playnite's plugin-settings storage immediately,
        // not only when the Settings dialog is later opened and saved.
        private ProfileService CreateProfileService()
        {
            if (settings?.Settings == null)
            {
                return null;
            }

            StremioVideoCenterSettings liveSettings = settings.Settings;

            return new ProfileService(
                liveSettings,
                () => SavePluginSettings(liveSettings));
        }

        // =========================================================
        // WATCH STATE SERVICE CONSTRUCTION (Slice 16B)
        // =========================================================
        //
        // Same reasoning and lifecycle as CreateProfileService() above -
        // one WatchStateService per Video Center window-open, built here
        // where the live settings instance and SavePluginSettings are
        // both available, saving through the exact same delegate
        // pattern. Also wires ProfileService.ProfileDeleted ->
        // WatchStateService.RemoveAllForProfile here, at construction
        // time, since this is the one place both services already exist
        // together - "a profile owns all personal media state" means
        // deleting a profile must not leave its Watch State orphaned,
        // without WatchStateService or ProfileService needing to know
        // about each other directly.
        private WatchStateService CreateWatchStateService(ProfileService profileService)
        {
            if (settings?.Settings == null)
            {
                return null;
            }

            StremioVideoCenterSettings liveSettings = settings.Settings;

            var watchStateService = new WatchStateService(
                liveSettings,
                () => SavePluginSettings(liveSettings));

            if (profileService != null)
            {
                profileService.ProfileDeleted +=
                    (sender, deletedProfileId) =>
                        watchStateService.RemoveAllForProfile(deletedProfileId);
            }

            return watchStateService;
        }

        // =========================================================
        // WATCH HISTORY SERVICE CONSTRUCTION (Slice 16C)
        // =========================================================
        //
        // Same lifecycle/save-delegate/ProfileDeleted-cleanup pattern as
        // CreateWatchStateService above - "History must also subscribe
        // to/profile-cleanup through the same lifecycle" (Slice 16C
        // spec, Section 24).
        private WatchHistoryService CreateWatchHistoryService(ProfileService profileService)
        {
            if (settings?.Settings == null)
            {
                return null;
            }

            StremioVideoCenterSettings liveSettings = settings.Settings;

            var watchHistoryService = new WatchHistoryService(
                liveSettings,
                () => SavePluginSettings(liveSettings));

            if (profileService != null)
            {
                profileService.ProfileDeleted +=
                    (sender, deletedProfileId) =>
                        watchHistoryService.RemoveAllForProfile(deletedProfileId);
            }

            return watchHistoryService;
        }

        // =========================================================
        // MY LIST SERVICE CONSTRUCTION (Slice 17A)
        // =========================================================
        //
        // Same lifecycle/save-delegate/ProfileDeleted-cleanup pattern as
        // CreateWatchStateService/CreateWatchHistoryService above -
        // "a profile owns all personal media state" means deleting a
        // profile must not leave its My List orphaned.
        private MyListService CreateMyListService(ProfileService profileService)
        {
            if (settings?.Settings == null)
            {
                return null;
            }

            StremioVideoCenterSettings liveSettings = settings.Settings;

            var myListService = new MyListService(
                liveSettings,
                () => SavePluginSettings(liveSettings));

            if (profileService != null)
            {
                profileService.ProfileDeleted +=
                    (sender, deletedProfileId) =>
                        myListService.RemoveAllForProfile(deletedProfileId);
            }

            return myListService;
        }

        // TEMPORARY DIAGNOSTIC (controller double-input investigation) -
        // logs every single raw invocation of this callback, before any
        // filtering, to StremioVideoCenter.log. Purpose: determine whether
        // Playnite itself calls this method more than once for one
        // physical D-pad press (e.g. two controller APIs/devices reporting
        // the same physical controller), independent of anything our own
        // code does afterward. Remove once the root cause is confirmed.
        private static int _diagCallSeq;

        public override void OnControllerButtonStateChanged(
            OnControllerButtonStateChangedArgs args)
        {
            int seq = System.Threading.Interlocked.Increment(ref _diagCallSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] #{0} button={1} state={2} windowNull={3} viewNull={4}",
                    seq,
                    args.Button,
                    args.State,
                    videoCenterWindow == null,
                    videoCenterView == null));

            if (videoCenterWindow == null ||
                videoCenterView == null)
            {
                return;
            }

            if (args.State != ControllerInputState.Pressed)
            {
                return;
            }

            // Give the Video Center first chance to consume controller
            // input. This keeps B inside the player and gives A a real
            // Select action for cards and player controls.
            if (videoCenterView.HandleControllerInput(args.Button))
            {
                return;
            }

            // If the Video Center did not consume B, close the window.
            if (args.Button == ControllerInput.B)
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

            // Lifecycle fix: stop the hero rotation loop before releasing
            // our reference to the view - StremioVideoCenterView owns the
            // rotation loop's lifetime; this is the one explicit place
            // (covering every close path: controller B, Escape/Back, and
            // any future one, since they all funnel through
            // CloseVideoCenter -> videoCenterWindow.Close() -> this
            // handler) that tells it to stop. Safe even if the view never
            // finished loading or the loop was never running - see
            // StopHeroBackdropRotation's own remarks.
            videoCenterView?.StopHeroBackdropRotation();

            videoCenterWindow = null;
            videoCenterView = null;

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