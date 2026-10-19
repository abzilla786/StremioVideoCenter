using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // ADDON MANIFEST MAPPER (Slice 20 Stage 1B)
    // =============================================================
    //
    // Turns a fetched manifest into the capability fields stored on an
    // AddonInstance. Shared by the install flow (which builds a new
    // instance) and the explicit refresh (which updates an existing one)
    // so both record the same things.
    public static class AddonManifestMapper
    {
        public static List<string> ExtractResourceNames(
            List<StremioManifestResource> resources)
        {
            List<string> names = new List<string>();

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

        // Keeps each resource's own types/idPrefixes (previously lost when
        // resources were flattened to names).
        public static List<AddonResourceDeclaration> ExtractResourceDeclarations(
            List<StremioManifestResource> resources)
        {
            List<AddonResourceDeclaration> declarations = new List<AddonResourceDeclaration>();

            if (resources == null)
            {
                return declarations;
            }

            foreach (StremioManifestResource resource in resources)
            {
                if (resource == null || string.IsNullOrWhiteSpace(resource.Name))
                {
                    continue;
                }

                declarations.Add(new AddonResourceDeclaration
                {
                    Name = resource.Name,
                    Types = resource.Types != null
                        ? new List<string>(resource.Types)
                        : new List<string>(),
                    IdPrefixes = resource.IdPrefixes != null
                        ? new List<string>(resource.IdPrefixes)
                        : new List<string>()
                });
            }

            return declarations;
        }

        // Sets only the fields Stage 1B adds. Used by the install flow,
        // which already sets every other manifest-derived field itself.
        public static void ApplyResourceDetails(
            AddonInstance instance,
            StremioAddonManifest manifest)
        {
            instance.Resources = ExtractResourceDeclarations(manifest.Resources);

            instance.Configurable =
                manifest.BehaviorHints != null &&
                manifest.BehaviorHints.Configurable == true;

            instance.ConfigurationRequired =
                manifest.BehaviorHints != null &&
                manifest.BehaviorHints.ConfigurationRequired == true;
        }

        // Updates the manifest-derived (capability snapshot) fields of an
        // existing instance. Deliberately does NOT touch what the user or
        // Zino decided: FriendlyName, Enabled, Roles, ProviderKey, IsCore,
        // Category, InstallPolicy and ManifestUrl all stay as they are.
        //
        // Returns the roles this refresh added (see
        // AddRolesForNewlyDeclaredResources), so the caller can tell the user.
        internal static List<string> ApplyManifest(
            AddonInstance instance,
            StremioAddonManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(instance.AddonId))
            {
                instance.AddonId = manifest.Id;
            }

            instance.Version = manifest.Version ?? string.Empty;
            instance.Description = manifest.Description ?? string.Empty;
            instance.Logo = manifest.Logo ?? string.Empty;
            List<string> previousResourceNames =
                instance.ResourceNames ?? new List<string>();

            instance.ResourceNames = ExtractResourceNames(manifest.Resources);
            instance.Types = manifest.Types ?? new List<string>();
            instance.IdPrefixes = manifest.IdPrefixes ?? new List<string>();

            ApplyResourceDetails(instance, manifest);

            return AddRolesForNewlyDeclaredResources(instance, previousResourceNames);
        }

        // A role is only ever ADDED, and only for a resource the addon did
        // not declare before this refresh (e.g. an addon update that starts
        // declaring "subtitles" gains the subtitle role). Roles the user or
        // Zino assigned are never removed or changed: instances with no
        // roles (unknown), core addons (roles assigned explicitly by Zino)
        // and instances with no earlier resource snapshot are left alone,
        // and a resource that was already declared before cannot add a role
        // (so an addon deliberately not used for streams stays that way).
        private static List<string> AddRolesForNewlyDeclaredResources(
            AddonInstance instance,
            List<string> previousResourceNames)
        {
            List<string> added = new List<string>();

            if (instance.IsCore ||
                instance.Roles == null ||
                instance.Roles.Count == 0 ||
                previousResourceNames.Count == 0)
            {
                return added;
            }

            List<string> newlyDeclared = instance.ResourceNames.FindAll(name =>
                !previousResourceNames.Contains(name));

            foreach (string role in AddonRole.InferFromResourceNames(newlyDeclared))
            {
                if (!instance.Roles.Contains(role))
                {
                    instance.Roles.Add(role);
                    added.Add(role);
                }
            }

            return added;
        }
    }

    // =============================================================
    // MANIFEST REFRESH (Slice 20 Stage 1B)
    // =============================================================

    public enum ManifestRefreshStatus
    {
        Refreshed,
        Failed,
        TimedOut,
        Cancelled,
        InvalidManifest,
        IdentityChanged
    }

    // Message is a fixed, user-safe sentence: never an exception message,
    // URL or addon configuration.
    public sealed class ManifestRefreshResult
    {
        public ManifestRefreshStatus Status { get; set; }

        public string Message { get; set; }

        public bool Succeeded
        {
            get { return Status == ManifestRefreshStatus.Refreshed; }
        }
    }

    // Explicit, user-triggered refresh of one installed addon's manifest
    // (never run automatically, e.g. at startup). The fetch is bounded by
    // StremioAddonService's manifest timeout and cancellable. The
    // instance is changed only after a complete, valid manifest has been
    // fetched and mapped, so any failure leaves the last valid manifest
    // data exactly as it was - the addon is never removed or disabled
    // because it was temporarily unreachable.
    public static class AddonManifestRefresh
    {
        public static async Task<ManifestRefreshResult> RefreshAsync(
            AddonInstance instance,
            CancellationToken cancellationToken)
        {
            if (instance == null || string.IsNullOrWhiteSpace(instance.ManifestUrl))
            {
                return Result(
                    ManifestRefreshStatus.Failed,
                    "This addon has no address to refresh from.");
            }

            string logName = StremioAddonService.SafeLogName(instance.FriendlyName);

            try
            {
                StremioAddonService service = new StremioAddonService(instance.ManifestUrl);

                StremioAddonManifest manifest =
                    await service.GetManifestAsync(cancellationToken);

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    Log(logName, ManifestRefreshStatus.InvalidManifest, null);

                    return Result(
                        ManifestRefreshStatus.InvalidManifest,
                        "The addon did not return a valid manifest. Its previous details were kept.");
                }

                // A manifest with no usable resource declarations is not a
                // valid manifest (the protocol requires "resources"), and
                // StremioAddonService.ParseResources also yields an empty
                // list when it cannot read that field. Applying it would
                // clear the stored resource names, types and prefixes, so
                // the previous details are kept instead. Optional fields
                // (types, idPrefixes, behaviorHints, description, logo,
                // version) being absent is normal and is not checked here.
                if (AddonManifestMapper.ExtractResourceNames(manifest.Resources).Count == 0)
                {
                    Log(logName, ManifestRefreshStatus.InvalidManifest, null);

                    return Result(
                        ManifestRefreshStatus.InvalidManifest,
                        "The addon's manifest did not declare any usable resources. Its previous details were kept.");
                }

                // A different addon id at the same address means something
                // else is now being served there; do not silently adopt it.
                if (!string.IsNullOrWhiteSpace(instance.AddonId) &&
                    !string.Equals(instance.AddonId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Log(logName, ManifestRefreshStatus.IdentityChanged, null);

                    return Result(
                        ManifestRefreshStatus.IdentityChanged,
                        "The addon now identifies as a different addon. Its previous details were kept.");
                }

                List<string> addedRoles = AddonManifestMapper.ApplyManifest(instance, manifest);

                Log(logName, ManifestRefreshStatus.Refreshed, null);

                return Result(
                    ManifestRefreshStatus.Refreshed,
                    "Manifest refreshed" +
                    (string.IsNullOrWhiteSpace(instance.Version) ? string.Empty : " (v" + instance.Version + ")") +
                    "." +
                    DescribeAddedRoles(addedRoles));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log(logName, ManifestRefreshStatus.Cancelled, null);

                return Result(ManifestRefreshStatus.Cancelled, "Refresh cancelled.");
            }
            catch (TimeoutException)
            {
                Log(logName, ManifestRefreshStatus.TimedOut, null);

                return Result(
                    ManifestRefreshStatus.TimedOut,
                    "The addon did not respond in time. Its previous details were kept.");
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
            {
                Log(logName, ManifestRefreshStatus.TimedOut, null);

                return Result(
                    ManifestRefreshStatus.TimedOut,
                    "The addon did not respond in time. Its previous details were kept.");
            }
            catch (Exception ex)
            {
                // Unreachable, HTTP error or unreadable JSON. The exception
                // text is logged with URLs/paths removed and never shown.
                Log(logName, ManifestRefreshStatus.Failed, UserFacingErrors.ForLog(ex));

                return Result(
                    ManifestRefreshStatus.Failed,
                    "Could not refresh - the addon may be unreachable or returned an invalid manifest. Its previous details were kept.");
            }
        }

        // Only the two roles that change what Zino asks the addon for are
        // announced; a fixed sentence, never addon-supplied text.
        private static string DescribeAddedRoles(List<string> addedRoles)
        {
            bool streams = addedRoles.Contains(AddonRole.StreamProvider);
            bool subtitles = addedRoles.Contains(AddonRole.SubtitleProvider);

            if (streams && subtitles)
            {
                return " It will now also be used for streams and subtitles.";
            }

            if (streams)
            {
                return " It will now also be used for streams.";
            }

            if (subtitles)
            {
                return " It will now also be used for subtitles.";
            }

            return string.Empty;
        }

        private static ManifestRefreshResult Result(ManifestRefreshStatus status, string message)
        {
            return new ManifestRefreshResult { Status = status, Message = message };
        }

        private static void Log(string addonName, ManifestRefreshStatus status, string diagnostic)
        {
            StremioVideoCenterLogger.Info(
                string.Format(
                    "[ADDON-REFRESH] addon={0} status={1}{2}",
                    addonName,
                    status,
                    string.IsNullOrEmpty(diagnostic) ? string.Empty : " ex=" + diagnostic));
        }
    }
}
