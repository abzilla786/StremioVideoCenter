using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace StremioVideoCenter
{
    public partial class StremioVideoCenterSettingsView : UserControl
    {
        private bool isLoadingValues = false;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public StremioVideoCenterSettingsView()
        {
            InitializeComponent();

            Loaded += SettingsView_Loaded;
        }

        // =========================================================
        // LOAD SAVED VALUES
        // =========================================================

        private void SettingsView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            LoadSettingsIntoPasswordBoxes();
        }

        // =========================================================
        // LOAD SETTINGS INTO UI
        // =========================================================

        private void LoadSettingsIntoPasswordBoxes()
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel == null ||
                viewModel.Settings == null)
            {
                return;
            }

            isLoadingValues = true;

            TmdbApiKeyBox.Password =
                viewModel.Settings.TmdbApiKey ?? string.Empty;

            // OMDb's PasswordBox was removed from this panel (Cinemeta-
            // First Addon Settings Architecture slice - see the XAML
            // remarks above the Real-Debrid section). Settings.OmdbApiKey
            // itself is left untouched here; nothing in this view reads
            // or writes it any more.

            RealDebridApiKeyBox.Password =
                viewModel.Settings.RealDebridApiKey ?? string.Empty;

            // The legacy "Stremio Addons" free-text box was removed from
            // this panel in a later correction pass - Settings.StremioAddonUrls
            // is no longer loaded into any control here. The setting
            // itself, its default value, and its use as a playback-time
            // fallback (StremioVideoCenterView.xaml.cs) are untouched.

            isLoadingValues = false;

            RefreshAddonInstancesList();
        }

        // =========================================================
        // TMDB PASSWORD CHANGED
        // =========================================================

        private void TmdbApiKeyBox_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (isLoadingValues)
            {
                return;
            }

            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            viewModel.Settings.TmdbApiKey =
                TmdbApiKeyBox.Password;

            TmdbConnectionStatus.Text =
                string.Empty;
        }

        // =========================================================
        // REAL-DEBRID PASSWORD CHANGED
        // =========================================================

        private void RealDebridApiKeyBox_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (isLoadingValues)
            {
                return;
            }

            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            viewModel.Settings.RealDebridApiKey =
                RealDebridApiKeyBox.Password;

            RealDebridConnectionStatus.Text =
                string.Empty;
        }

        // =========================================================
        // TEST TMDB CONNECTION
        // =========================================================

        private async void TestTmdbConnection_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string accessToken =
                viewModel.Settings.TmdbApiKey;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                TmdbConnectionStatus.Text =
                    "Please enter your TMDB API Read Access Token first.";

                return;
            }

            TmdbConnectionStatus.Text =
                "Testing TMDB connection...";

            TestTmdbConnectionButton.IsEnabled = false;

            try
            {
                var tmdbService =
                    new TmdbService(accessToken);

                bool connected =
                    await tmdbService.TestConnectionAsync();

                if (connected)
                {
                    TmdbConnectionStatus.Text =
                        "TMDB connection successful.";
                }
                else
                {
                    TmdbConnectionStatus.Text =
                        "TMDB connection failed. Please check your credential and internet connection.";
                }
            }
            catch
            {
                TmdbConnectionStatus.Text =
                    "TMDB connection failed.";
            }
            finally
            {
                TestTmdbConnectionButton.IsEnabled = true;
            }
        }
        // =========================================================
        // TEST REAL-DEBRID CONNECTION
        // =========================================================

        private async void TestRealDebridConnection_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string apiToken =
                viewModel.Settings.RealDebridApiKey;

            if (string.IsNullOrWhiteSpace(apiToken))
            {
                RealDebridConnectionStatus.Text =
                    "Please enter your Real-Debrid API token first.";

                return;
            }

            RealDebridConnectionStatus.Text =
                "Testing Real-Debrid connection...";

            TestRealDebridConnectionButton.IsEnabled = false;

            try
            {
                var realDebridService =
                    new RealDebridService(apiToken);

                bool connected =
                    await realDebridService.TestConnectionAsync();

                if (connected)
                {
                    RealDebridConnectionStatus.Text =
                        "Real-Debrid connection successful.";
                }
                else
                {
                    RealDebridConnectionStatus.Text =
                        "Real-Debrid connection failed. Please check your API token and internet connection.";
                }
            }
            catch
            {
                RealDebridConnectionStatus.Text =
                    "Real-Debrid connection failed.";
            }
            finally
            {
                TestRealDebridConnectionButton.IsEnabled = true;
            }
        }

        // =========================================================
        // ADDON MANAGEMENT (STRUCTURED ADDON INSTANCES)
        // =========================================================
        //
        // The legacy "Test Configured Addons" button (which tested every
        // URL in the now-removed StremioAddonUrls textbox) was removed
        // in a later correction pass along with that textbox. Every
        // entry point below - Torrentio's paste-back box, Comet's
        // paste-back box, and the generic Custom box - funnels through
        // the same InstallAddonFromManifestUrlAsync helper. There is no
        // Torrentio- or Comet-specific parsing/API logic anywhere here;
        // "Configure & Install"/"Reconfigure" only opens each addon's own
        // configuration page in the system browser (per the Stremio
        // addon protocol's own behaviorHints.configurable convention)
        // and the user pastes back whatever manifest URL that page
        // gives them, exactly like the Custom flow.

        private void TorrentioConfigure_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenExternalConfigurePage(
                "https://torrentio.strem.fun/configure",
                TorrentioInstallStatus);
        }

        private void CometConfigure_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenExternalConfigurePage(
                "https://comet.elfhosted.com/configure",
                CometInstallStatus);
        }

        private void OpenExternalConfigurePage(
            string url,
            TextBlock statusText)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                statusText.Text =
                    "Opened in your browser. Configure the addon there, " +
                    "then paste the manifest URL it gives you below and click Install.";
            }
            catch (Exception ex)
            {
                statusText.Text =
                    "Could not open the configuration page automatically (" +
                    ex.Message +
                    "). You can open it yourself: " +
                    url;
            }
        }

        private async void TorrentioInstall_Click(
            object sender,
            RoutedEventArgs e)
        {
            await InstallAddonFromManifestUrlAsync(
                TorrentioManifestUrlBox.Text,
                "Recommended",
                "torrentio",
                AddonInstallPolicy.ReplaceExisting,
                "Torrentio",
                TorrentioInstallStatus,
                TorrentioInstallButton);
        }

        private async void CometInstall_Click(
            object sender,
            RoutedEventArgs e)
        {
            await InstallAddonFromManifestUrlAsync(
                CometManifestUrlBox.Text,
                "Recommended",
                "comet",
                AddonInstallPolicy.ReplaceExisting,
                "Comet",
                CometInstallStatus,
                CometInstallButton);
        }

        private async void CustomInstall_Click(
            object sender,
            RoutedEventArgs e)
        {
            await InstallAddonFromManifestUrlAsync(
                CustomManifestUrlBox.Text,
                "Optional",
                null,
                AddonInstallPolicy.AllowMultiple,
                null,
                CustomInstallStatus,
                CustomInstallButton);
        }

        // Fetches and validates a manifest, then saves it as a new
        // AddonInstance. Never partially saves - the instance is only
        // added to Settings.AddonInstances (via InstallOrReplaceAddonInstance)
        // after the manifest has been successfully fetched and its
        // required "id" field confirmed present. Any failure (unreachable
        // addon, malformed JSON, missing required fields) produces a
        // clear status message and leaves Settings.AddonInstances
        // completely unchanged - the previous, working configuration (if
        // any) is never touched until a new one is already known-good.
        //
        // providerKey/installPolicy drive replace-vs-add semantics
        // (Cinemeta-First Addon Settings Architecture slice): Torrentio
        // and Comet pass a fixed providerKey with ReplaceExisting, so
        // re-pasting a new manifest URL replaces the previous instance
        // rather than creating a second "Torrentio"/"Comet" entry. Custom
        // addons pass a null providerKey with AllowMultiple, preserving
        // the original add-only behaviour (and its own manifest-URL
        // duplicate guard below) so several differently-configured
        // generic addons can coexist.
        private async Task InstallAddonFromManifestUrlAsync(
            string manifestUrlInput,
            string category,
            string providerKey,
            string installPolicy,
            string statusLabel,
            TextBlock statusText,
            Button installButton)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            if (viewModel.Settings.AddonInstances == null)
            {
                viewModel.Settings.AddonInstances = new List<AddonInstance>();
            }

            if (string.IsNullOrWhiteSpace(manifestUrlInput))
            {
                statusText.Text = "Please paste a manifest URL first.";
                return;
            }

            string trimmedUrl = manifestUrlInput.Trim();

            // StremioAddonService wants the addon's base URL and appends
            // "/manifest.json" itself - accept either shape so a
            // straight copy-paste of the full manifest.json link works
            // too.
            const string manifestSuffix = "/manifest.json";

            string baseUrl =
                trimmedUrl.EndsWith(manifestSuffix, StringComparison.OrdinalIgnoreCase)
                    ? trimmedUrl.Substring(0, trimmedUrl.Length - manifestSuffix.Length)
                    : trimmedUrl.TrimEnd('/');

            bool isReplacePolicy =
                string.Equals(installPolicy, AddonInstallPolicy.ReplaceExisting, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(providerKey);

            // The "already installed, exact same URL" guard only makes
            // sense for AllowMultiple (Custom) addons - for
            // ReplaceExisting providers (Torrentio/Comet) re-pasting the
            // SAME url the user already has configured should still be
            // allowed through (it's a harmless no-op replace), and a
            // DIFFERENT url must always proceed to replace the old one.
            if (!isReplacePolicy)
            {
                bool alreadyInstalled = viewModel.Settings.AddonInstances.Exists(instance =>
                    instance != null &&
                    string.Equals(instance.ManifestUrl, baseUrl, StringComparison.OrdinalIgnoreCase));

                if (alreadyInstalled)
                {
                    statusText.Text = "This addon is already installed.";
                    return;
                }
            }

            statusText.Text = "Fetching and validating manifest...";
            installButton.IsEnabled = false;

            try
            {
                var service = new StremioAddonService(baseUrl);

                StremioAddonManifest manifest = await service.GetManifestAsync();

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    statusText.Text =
                        "This addon did not return a valid manifest (missing required fields). " +
                        "Your previous configuration, if any, was left unchanged.";

                    return;
                }

                var instance = new AddonInstance
                {
                    // Never fall back to baseUrl here: for a Real-Debrid-
                    // configured addon (Torrentio, Comet, or any future
                    // provider added this same generic way) the manifest
                    // URL carries the user's RD key, and FriendlyName is
                    // exactly what Source Selector/diagnostics display
                    // (see SourceAddonName). Prefer the fixed statusLabel
                    // (e.g. "Torrentio") over the manifest's own possibly-
                    // generic name so re-configuring always shows the same
                    // recognisable identity; fall back to the manifest
                    // name/id for Custom addons, which have no fixed label.
                    FriendlyName = !string.IsNullOrWhiteSpace(statusLabel)
                        ? statusLabel
                        : (string.IsNullOrWhiteSpace(manifest.Name)
                            ? manifest.Id
                            : manifest.Name),
                    AddonId = manifest.Id,
                    ManifestUrl = baseUrl,
                    Enabled = true,
                    Category = category,
                    ProviderKey = providerKey ?? string.Empty,
                    InstallPolicy = installPolicy ?? AddonInstallPolicy.AllowMultiple,
                    Version = manifest.Version ?? string.Empty,
                    Description = manifest.Description ?? string.Empty,
                    Logo = manifest.Logo ?? string.Empty,
                    Types = manifest.Types ?? new List<string>(),
                    IdPrefixes = manifest.IdPrefixes ?? new List<string>(),
                    ResourceNames = ExtractResourceNames(manifest.Resources)
                };

                if (isReplacePolicy)
                {
                    viewModel.Settings.InstallOrReplaceAddonInstance(instance);
                }
                else
                {
                    viewModel.Settings.AddonInstances.Add(instance);
                }

                RefreshAddonInstancesList();

                statusText.Text =
                    "Installed: " +
                    instance.FriendlyName +
                    (string.IsNullOrWhiteSpace(instance.Version)
                        ? string.Empty
                        : " v" + instance.Version);

                if (manifestUrlInput == TorrentioManifestUrlBox.Text)
                {
                    TorrentioManifestUrlBox.Text = string.Empty;
                }
                else if (manifestUrlInput == CometManifestUrlBox.Text)
                {
                    CometManifestUrlBox.Text = string.Empty;
                }
                else if (manifestUrlInput == CustomManifestUrlBox.Text)
                {
                    CustomManifestUrlBox.Text = string.Empty;
                }
            }
            catch (Exception)
            {
                // Unreachable addon, malformed JSON, timeout, etc. Never
                // echo ex.Message here: for Torrentio/Comet it can embed
                // the failed request URL (which carries the user's
                // pasted, possibly credential-bearing manifest URL) -
                // e.g. a WebException's message includes the request URI.
                // A generic, addon-agnostic message is sufficient; nothing
                // was saved, and any previous working configuration for
                // this provider is untouched.
                statusText.Text =
                    "Could not install this addon - it may be unreachable or returned an " +
                    "invalid manifest. Your previous configuration, if any, was left unchanged.";
            }
            finally
            {
                installButton.IsEnabled = true;
            }
        }

        private List<string> ExtractResourceNames(
            List<StremioManifestResource> resources)
        {
            var names = new List<string>();

            if (resources == null)
            {
                return names;
            }

            foreach (StremioManifestResource resource in resources)
            {
                if (resource != null && !string.IsNullOrWhiteSpace(resource.Name))
                {
                    names.Add(resource.Name);
                }
            }

            return names;
        }

        private void RemoveAddonInstance_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings?.AddonInstances == null)
            {
                return;
            }

            var button = sender as Button;
            var instance = button?.Tag as AddonInstance;

            if (instance == null)
            {
                return;
            }

            viewModel.Settings.AddonInstances.Remove(instance);

            RefreshAddonInstancesList();
        }

        // Re-binds the installed-instances list to the current
        // AddonInstances collection. List<T> raises no change
        // notifications of its own, so this is called explicitly after
        // every Add/Remove rather than relying on automatic UI refresh.
        private void RefreshAddonInstancesList()
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            if (viewModel.Settings.AddonInstances == null)
            {
                viewModel.Settings.AddonInstances = new List<AddonInstance>();
            }

            AddonInstancesItemsControl.ItemsSource = null;
            AddonInstancesItemsControl.ItemsSource = viewModel.Settings.AddonInstances;

            NoAddonInstancesText.Visibility = viewModel.Settings.AddonInstances.Count > 0
                ? Visibility.Collapsed
                : Visibility.Visible;

            TorrentioCurrentStatus.Text =
                DescribeProviderStatus(viewModel.Settings.AddonInstances, "torrentio", "Torrentio");

            CometCurrentStatus.Text =
                DescribeProviderStatus(viewModel.Settings.AddonInstances, "comet", "Comet");

            ApplyProviderButtonState(
                viewModel.Settings.AddonInstances,
                "torrentio",
                TorrentioConfigureButton,
                TorrentioRemoveButton);

            ApplyProviderButtonState(
                viewModel.Settings.AddonInstances,
                "comet",
                CometConfigureButton,
                CometRemoveButton);
        }

        // Describes whether a ReplaceExisting provider (Torrentio/Comet)
        // currently has a configured instance, without ever showing its
        // manifest URL - only FriendlyName/Version, the same
        // credential-safe fields the installed-instances list itself
        // displays.
        private static string DescribeProviderStatus(
            List<AddonInstance> instances,
            string providerKey,
            string displayName)
        {
            AddonInstance existing = instances?.Find(instance =>
                instance != null &&
                string.Equals(instance.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                return "Not configured.";
            }

            return "Configured" +
                (string.IsNullOrWhiteSpace(existing.Version) ? string.Empty : " (v" + existing.Version + ")") +
                (existing.Enabled ? string.Empty : " - disabled") +
                ".";
        }

        // Correction pass: state-appropriate button labels/visibility for
        // Torrentio/Comet, replacing the old always-visible "Install /
        // Reconfigure" wording (which also clipped at its original
        // width). Not configured -> only "Configure & Install..." is
        // shown; configured -> that same button relabels to "Reconfigure"
        // (it still just opens the provider's own configuration page -
        // TorrentioConfigure_Click/CometConfigure_Click are unchanged)
        // and a "Remove" button appears beside it.
        private static void ApplyProviderButtonState(
            List<AddonInstance> instances,
            string providerKey,
            Button configureButton,
            Button removeButton)
        {
            bool isConfigured = instances != null && instances.Exists(instance =>
                instance != null &&
                string.Equals(instance.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));

            configureButton.Content = isConfigured
                ? "Reconfigure"
                : "Configure & Install...";

            removeButton.Visibility = isConfigured
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // Removes the current Torrentio/Comet instance (if any) without
        // requiring the user to go through the paste-a-URL flow. Reuses
        // the same AddonInstances.Remove + RefreshAddonInstancesList path
        // as the generic per-row "Remove" button in the installed-
        // instances list below.
        private void RemoveProviderInstance(string providerKey)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings?.AddonInstances == null)
            {
                return;
            }

            AddonInstance existing = viewModel.Settings.AddonInstances.Find(instance =>
                instance != null &&
                string.Equals(instance.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                return;
            }

            viewModel.Settings.AddonInstances.Remove(existing);

            RefreshAddonInstancesList();
        }

        private void TorrentioRemove_Click(
            object sender,
            RoutedEventArgs e)
        {
            RemoveProviderInstance("torrentio");
        }

        private void CometRemove_Click(
            object sender,
            RoutedEventArgs e)
        {
            RemoveProviderInstance("comet");
        }

    }
}