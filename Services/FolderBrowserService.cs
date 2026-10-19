using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // FOLDER BROWSER SERVICE (Local Files - in-app folder browser)
    // =============================================================
    //
    // Filesystem listing for FolderBrowserView: the available drives, and
    // the sub-folders of one directory. No UI, no state, no settings, and no
    // scanning - choosing a folder hands the path to the existing
    // LocalLibraryService/LocalLibraryManager.AddFolder, nothing else.
    //
    // Safety:
    //   - Reparse points (junctions, symbolic links, mounted-folder points)
    //     are never listed, so navigation cannot enter a link and cannot loop.
    //   - Hidden and system folders ($RECYCLE.BIN, System Volume Information,
    //     ...) are not listed.
    //   - Every failure (missing, access denied, drive gone, I/O error) is a
    //     status, never an exception. Nothing here logs or returns an
    //     exception message: it can embed the path.
    // Blocking I/O (a disconnected share can stall for a long time), so the
    // caller runs these on a background thread.
    public enum FolderListingStatus
    {
        Ok = 0,
        NotFound = 1,
        AccessDenied = 2,
        Unavailable = 3
    }

    public sealed class FolderEntry
    {
        public string Name { get; set; }

        public string Path { get; set; }

        // Second line for a drive ("Local disk - Data"); empty for folders.
        public string Detail { get; set; }
    }

    public sealed class FolderListing
    {
        public FolderListing()
        {
            Entries = new List<FolderEntry>();
        }

        public FolderListingStatus Status { get; set; }

        public List<FolderEntry> Entries { get; private set; }

        // True when more folders exist than MaxEntries.
        public bool Truncated { get; set; }
    }

    public static class FolderBrowserService
    {
        public const int MaxEntries = 1000;

        public static List<FolderEntry> GetDrives()
        {
            var drives = new List<FolderEntry>();

            DriveInfo[] all;

            try
            {
                all = DriveInfo.GetDrives();
            }
            catch (Exception)
            {
                return drives;
            }

            foreach (DriveInfo drive in all)
            {
                try
                {
                    if (drive.DriveType == DriveType.NoRootDirectory ||
                        drive.DriveType == DriveType.Unknown)
                    {
                        continue;
                    }

                    drives.Add(new FolderEntry
                    {
                        Name = drive.Name,
                        Path = drive.Name,
                        Detail = DescribeDrive(drive)
                    });
                }
                catch (Exception)
                {
                    // A drive that cannot even be described is skipped.
                }
            }

            return drives;
        }

        private static string DescribeDrive(DriveInfo drive)
        {
            string kind;

            switch (drive.DriveType)
            {
                case DriveType.Removable: kind = "Removable drive"; break;
                case DriveType.Network: kind = "Network drive"; break;
                case DriveType.CDRom: kind = "Disc drive"; break;
                default: kind = "Local disk"; break;
            }

            try
            {
                if (!drive.IsReady)
                {
                    return kind + " - not ready";
                }

                string label = drive.VolumeLabel;

                return string.IsNullOrWhiteSpace(label) ? kind : kind + " - " + label.Trim();
            }
            catch (Exception)
            {
                return kind + " - not available";
            }
        }

        public static FolderListing List(string path)
        {
            var listing = new FolderListing();

            if (string.IsNullOrWhiteSpace(path))
            {
                listing.Status = FolderListingStatus.NotFound;
                return listing;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    listing.Status = FolderListingStatus.NotFound;
                    return listing;
                }

                var found = new List<FolderEntry>();

                foreach (DirectoryInfo directory in new DirectoryInfo(path).EnumerateDirectories())
                {
                    try
                    {
                        FileAttributes attributes = directory.Attributes;

                        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                            (attributes & FileAttributes.Hidden) != 0 ||
                            (attributes & FileAttributes.System) != 0)
                        {
                            continue;
                        }

                        found.Add(new FolderEntry
                        {
                            Name = directory.Name,
                            Path = directory.FullName,
                            Detail = string.Empty
                        });
                    }
                    catch (Exception)
                    {
                        // One unreadable entry never hides the others.
                    }
                }

                found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                listing.Truncated = found.Count > MaxEntries;
                listing.Entries.AddRange(found.Take(MaxEntries));
                listing.Status = FolderListingStatus.Ok;
            }
            catch (UnauthorizedAccessException)
            {
                listing.Entries.Clear();
                listing.Status = FolderListingStatus.AccessDenied;
            }
            catch (DirectoryNotFoundException)
            {
                listing.Entries.Clear();
                listing.Status = FolderListingStatus.NotFound;
            }
            catch (Exception)
            {
                // I/O errors, a drive removed mid-listing, a dropped share.
                listing.Entries.Clear();
                listing.Status = FolderListingStatus.Unavailable;
            }

            return listing;
        }

        // The folder above path, or null when path is a drive / share root
        // (the caller then shows the drive list).
        public static string GetParent(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                string trimmed = path.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar);

                // "C:" (a root with its separator trimmed) or empty.
                if (trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':'))
                {
                    return null;
                }

                string root = System.IO.Path.GetPathRoot(path);

                if (!string.IsNullOrEmpty(root) &&
                    string.Equals(
                        root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                        trimmed,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                DirectoryInfo parent = Directory.GetParent(trimmed);

                return parent != null ? parent.FullName : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
