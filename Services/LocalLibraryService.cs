using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL LIBRARY SERVICE (Slice 15 - architecture foundation)
    // =============================================================
    //
    // Owns the "Library Locations" concept of the Local Media
    // architecture - add/remove/list the folders the user has designated
    // as local media sources. This is deliberately the ONLY class that
    // touches StremioVideoCenterSettings.LibraryLocations, mirroring how
    // AddonInstances is only ever mutated through
    // StremioVideoCenterSettings.InstallOrReplaceAddonInstance/direct
    // list access from the Settings view model - a single, well-defined
    // owner for this persisted state.
    //
    // Deliberately thin: no scanning, no identification, no metadata
    // matching happens here - see LocalMediaScanService/
    // LocalMediaIdentificationService for those distinct concerns. This
    // class's only job is "which folders does the user want scanned,"
    // which is exactly what a caller (LocalMediaView today; a future
    // Local Media slice's browsing UI later) needs to drive the rest of
    // the pipeline.
    public class LocalLibraryService
    {
        private readonly StremioVideoCenterSettings _settings;

        public LocalLibraryService(StremioVideoCenterSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _settings = settings;

            if (_settings.LibraryLocations == null)
            {
                _settings.LibraryLocations = new List<LibraryLocation>();
            }
        }

        // =========================================================
        // LIST
        // =========================================================

        public IReadOnlyList<LibraryLocation> GetLocations()
        {
            return _settings.LibraryLocations.ToList();
        }

        // =========================================================
        // ADD
        // =========================================================
        //
        // Returns false without adding when the path is empty, already
        // present (case-insensitive), or does not currently exist as a
        // real directory - callers should surface that honestly rather
        // than silently adding an unusable location.
        public bool AddLocation(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = path.Trim();

            bool alreadyPresent = _settings.LibraryLocations.Any(location =>
                location != null &&
                string.Equals(location.Path, normalized, StringComparison.OrdinalIgnoreCase));

            if (alreadyPresent)
            {
                return false;
            }

            if (!Directory.Exists(normalized))
            {
                return false;
            }

            _settings.LibraryLocations.Add(new LibraryLocation
            {
                Path = normalized,
                DisplayName = new DirectoryInfo(normalized).Name,
                AddedUtc = DateTime.UtcNow
            });

            return true;
        }

        // =========================================================
        // REMOVE
        // =========================================================

        public bool RemoveLocation(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            int removed = _settings.LibraryLocations.RemoveAll(location =>
                location != null &&
                string.Equals(location.Path, path, StringComparison.OrdinalIgnoreCase));

            return removed > 0;
        }
    }
}
