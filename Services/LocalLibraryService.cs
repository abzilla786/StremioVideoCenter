using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL LIBRARY SERVICE
    // =============================================================
    //
    // Owns the "Library Locations" concept of the Local Files feature -
    // add / remove / list the folders the user has designated as local
    // media sources. This is the ONLY class that touches
    // StremioVideoCenterSettings.LibraryLocations, mirroring how
    // AddonInstances is only ever mutated through the Settings view
    // model - a single, well-defined owner for this persisted state.
    // Locations are global (not profile-owned).
    //
    // Deliberately thin: no scanning, no identification, no metadata
    // matching happens here. It mutates the in-memory settings list only;
    // the caller (LocalLibraryManager) persists with the plugin's existing
    // settings save mechanism after a successful change.
    //
    // PATH NORMALISATION
    // ------------------
    // Paths are compared case-insensitively (Windows) after being made
    // absolute and stripped of trailing separators, so "D:\Movies",
    // "d:\movies\" and "D:/Movies" are one location. UNC paths
    // ("\\NAS\Movies") are valid and kept as UNC. A location nested inside
    // another (or containing another) is refused as an overlap: the same
    // file would otherwise be indexed twice.
    public enum AddLocationResult
    {
        Added = 0,
        Duplicate = 1,
        Overlaps = 2,
        Invalid = 3,
        NotAccessible = 4
    }

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
            return _settings.LibraryLocations.Where(l => l != null && !string.IsNullOrWhiteSpace(l.Path)).ToList();
        }

        // =========================================================
        // NORMALISE
        // =========================================================
        //
        // Returns the normalised absolute path, or null when the text is
        // not a usable absolute path. Does not touch the filesystem.
        public static string NormalisePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string trimmed = path.Trim().Trim('"');

            if (trimmed.Length == 0)
            {
                return null;
            }

            try
            {
                if (!Path.IsPathRooted(trimmed))
                {
                    return null;
                }

                string full = Path.GetFullPath(trimmed);

                return LocalMediaScanService.TrimTrailingSeparators(full);
            }
            catch (Exception)
            {
                // ArgumentException / NotSupportedException / PathTooLong:
                // not a usable path.
                return null;
            }
        }

        public static bool PathsEqual(string a, string b)
        {
            return string.Equals(
                LocalMediaScanService.TrimTrailingSeparators(a),
                LocalMediaScanService.TrimTrailingSeparators(b),
                StringComparison.OrdinalIgnoreCase);
        }

        // True when child equals parent or lies below it.
        public static bool IsSameOrBelow(string child, string parent)
        {
            string c = LocalMediaScanService.TrimTrailingSeparators(child);
            string p = LocalMediaScanService.TrimTrailingSeparators(parent);

            if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(p))
            {
                return false;
            }

            if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefix = p.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? p
                : p + Path.DirectorySeparatorChar;

            return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================
        // ADD
        // =========================================================
        //
        // Reports exactly why a location was not added so the caller can
        // say so honestly rather than silently doing nothing.
        public AddLocationResult TryAddLocation(string path)
        {
            string normalised = NormalisePath(path);

            if (normalised == null)
            {
                return AddLocationResult.Invalid;
            }

            foreach (LibraryLocation existing in GetLocations())
            {
                if (PathsEqual(existing.Path, normalised))
                {
                    return AddLocationResult.Duplicate;
                }

                string existingNormalised = NormalisePath(existing.Path) ?? existing.Path;

                if (IsSameOrBelow(normalised, existingNormalised) ||
                    IsSameOrBelow(existingNormalised, normalised))
                {
                    return AddLocationResult.Overlaps;
                }
            }

            try
            {
                if (!Directory.Exists(normalised))
                {
                    return AddLocationResult.NotAccessible;
                }
            }
            catch (Exception)
            {
                return AddLocationResult.NotAccessible;
            }

            string displayName;

            try
            {
                displayName = new DirectoryInfo(normalised).Name;
            }
            catch (Exception)
            {
                displayName = normalised;
            }

            _settings.LibraryLocations.Add(new LibraryLocation
            {
                Path = normalised,
                DisplayName = displayName,
                AddedUtc = DateTime.UtcNow
            });

            return AddLocationResult.Added;
        }

        // Existing contract (true only when a location was added).
        public bool AddLocation(string path)
        {
            return TryAddLocation(path) == AddLocationResult.Added;
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
                location != null && PathsEqual(location.Path, path));

            return removed > 0;
        }

        // =========================================================
        // FOLDER STATE
        // =========================================================
        //
        // Checks each location on the calling thread. Directory.Exists on
        // a disconnected network share can block for a long time, so the
        // caller (LocalLibraryManager) runs this on a background thread.
        public List<LocalFolderStatus> GetFolderStatuses(Func<string, int> itemCountFor)
        {
            var statuses = new List<LocalFolderStatus>();

            foreach (LibraryLocation location in GetLocations())
            {
                LocalFolderState state;

                try
                {
                    state = Directory.Exists(location.Path)
                        ? LocalFolderState.Accessible
                        : LocalFolderState.Missing;
                }
                catch (Exception)
                {
                    state = LocalFolderState.Unavailable;
                }

                string displayName = string.IsNullOrWhiteSpace(location.DisplayName)
                    ? location.Path
                    : location.DisplayName;

                statuses.Add(new LocalFolderStatus
                {
                    Path = location.Path,
                    DisplayName = displayName,
                    State = state,
                    ItemCount = itemCountFor != null ? itemCountFor(location.Path) : 0
                });
            }

            return statuses;
        }
    }
}
