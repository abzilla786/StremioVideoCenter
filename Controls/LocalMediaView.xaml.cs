using Playnite.SDK.Events;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace StremioVideoCenterControls
{
    // =============================================================
    // LOCAL MEDIA VIEW
    // =============================================================
    //
    // Cinemeta-First Addon Settings Architecture slice addendum
    // ("LOCAL MEDIA NAVIGATION"). Local Files is a CORE/DEFAULT addon
    // (see CoreAddonSeedingService), so a dedicated navigation entry
    // point for it must exist on every fresh install, independent of
    // TMDB/Torrentio/Comet configuration - this view and its host
    // (StremioVideoCenterView.LocalMediaHost/ShowLocalMediaAsync) are
    // exactly that entry point.
    //
    // Deliberately NOT a full local-media browsing UI. This codebase has
    // no local-file catalogue/scan-listing functionality today - only
    // stream RESOLUTION via StremioAddonRegistry against whatever is
    // listening on the Local Files addon's loopback endpoint (see
    // CoreAddonSeedingService's Local Files definition and
    // StremioAddonRegistry's per-addon try/catch, which already handles
    // "nothing listening" gracefully). Building a real browsing grid here
    // would mean either fabricating catalogue data (explicitly forbidden
    // by this task) or inventing a brand-new local-library scanning
    // system (explicitly out of scope - "do not build a completely new
    // local-media browsing system if the underlying browsing
    // functionality doesn't yet exist"). So this view only shows an
    // honest status message and Initialize(bool) reflects whether the
    // Local Files addon instance is currently present/enabled in
    // Settings - a real, already-known fact, not an invented one.
    //
    // Same overlay architecture as EpisodeBrowserView/SourceSelectorView:
    // a UserControl added into its own host Grid, with a Closed event
    // bridged into an awaitable Task by the caller (mirrors
    // BrowsingCancelled there), and the same
    // PreviewKeyDown/HandleControllerInput(ControllerInput) shape so it
    // participates in StremioVideoCenterView's existing "most specific
    // overlay wins" input-priority chain unchanged.
    public partial class LocalMediaView : UserControl
    {
        public event EventHandler Closed;

        public LocalMediaView()
        {
            InitializeComponent();

            // Dedicated-page Back navigation task: this view has no
            // other focusable content besides the Back (Close) button -
            // it's a status page, not a browsable catalogue - so
            // keyboard/controller users need it focused by default
            // rather than left with nothing reachable via arrow keys.
            // Loaded (not the constructor) because the button isn't
            // arrangeable/focusable until the control is actually in the
            // visual tree - mirrors MediaCard's own Loaded-based pattern
            // elsewhere in this project.
            Loaded += LocalMediaView_Loaded;
        }

        private void LocalMediaView_Loaded(object sender, RoutedEventArgs e)
        {
            CloseButton.Focus();
        }

        // Sets the status text from real, already-known facts (whether
        // the "localfiles" core addon instance is currently present and
        // enabled, and how many Local Media library locations are
        // currently configured) - never fabricated catalogue content.
        // Safe to call before or after the view is shown.
        //
        // libraryLocationCount is a minimal, honest proof that Slice 15's
        // Local Media architecture (LocalLibraryService reading
        // StremioVideoCenterSettings.LibraryLocations) is actually wired
        // end-to-end from Settings through to this view - not a full
        // browsing UI. Adding/removing locations is not exposed here
        // this slice (see class remarks); that remains for a dedicated
        // future Local Media slice.
        public void Initialize(bool localFilesAddonConfigured, int libraryLocationCount)
        {
            StatusText.Text = localFilesAddonConfigured
                ? "The Local Files addon is installed and enabled."
                : "The Local Files addon is not currently installed or is disabled. " +
                  "Check Addon Management in Settings.";

            LibraryLocationsText.Text = libraryLocationCount == 0
                ? "No local media library locations are configured yet."
                : libraryLocationCount + " local media library location" +
                  (libraryLocationCount == 1 ? string.Empty : "s") + " configured.";
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void RaiseClosed()
        {
            EventHandler handler = Closed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void LocalMediaView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RaiseClosed();
                e.Handled = true;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            if (button == ControllerInput.B)
            {
                RaiseClosed();
                return true;
            }

            // Dedicated-page Back navigation task: lets controller A
            // activate the Back button (the only focusable control on
            // this page) - same generic focused-ButtonBase reuse as
            // every other dedicated page's own ActivateFocusedElement.
            if (button == ControllerInput.A &&
                Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return true;
            }

            return false;
        }
    }
}
