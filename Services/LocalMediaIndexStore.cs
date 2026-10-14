using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using StremioVideoCenter.Models;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL MEDIA INDEX STORE
    // =============================================================
    //
    // Persists the Local Files library index (LocalLibraryIndex) as one
    // JSON file in the plugin's own data folder - deliberately separate
    // from the plugin settings, which hold only the Library Locations. The
    // index can be large and is rebuilt from the folders at any time, so it
    // never goes through SavePluginSettings.
    //
    // The index file DOES hold the files' locations (it must, to play
    // them); it lives in the plugin data folder, is never copied into
    // settings, saved-source references, watch state or logs, and is never
    // displayed. Callers must treat its contents like the mpv log.
    //
    // Load never throws: a missing, unreadable or corrupt file yields an
    // empty index (the next scan rebuilds it; a corrupt file is kept aside
    // as ".bad" once so it can be inspected). Save is atomic (temp file +
    // replace) so a crash mid-write cannot leave a half-written index.
    // The store itself is not thread-safe; LocalLibraryManager serialises
    // all access.
    public sealed class LocalMediaIndexStore
    {
        private const string FolderName = "LocalFiles";
        private const string FileName = "library-index.json";

        private readonly string _filePath;

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            Converters = { new StringEnumConverter() }
        };

        public LocalMediaIndexStore(string pluginDataDirectory)
        {
            _filePath = string.IsNullOrWhiteSpace(pluginDataDirectory)
                ? null
                : Path.Combine(pluginDataDirectory, FolderName, FileName);
        }

        public bool IsAvailable
        {
            get { return _filePath != null; }
        }

        // =========================================================
        // LOAD
        // =========================================================

        public LocalLibraryIndex Load()
        {
            if (_filePath == null || !File.Exists(_filePath))
            {
                return new LocalLibraryIndex();
            }

            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                LocalLibraryIndex index = JsonConvert.DeserializeObject<LocalLibraryIndex>(json, SerializerSettings);

                if (index == null || index.Version != LocalLibraryIndex.CurrentVersion || index.Items == null)
                {
                    return new LocalLibraryIndex();
                }

                index.Items.RemoveAll(item => item == null || string.IsNullOrWhiteSpace(item.Id));

                foreach (LocalLibraryItem item in index.Items)
                {
                    if (item.Candidates == null)
                    {
                        item.Candidates = new System.Collections.Generic.List<LocalMatchCandidate>();
                    }
                }

                return index;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "Local Files: the library index could not be read and will be rebuilt by the next scan. " +
                    UserFacingErrors.ForLog(ex));

                TryKeepCorruptCopy();

                return new LocalLibraryIndex();
            }
        }

        // =========================================================
        // SAVE
        // =========================================================
        //
        // Returns false (and logs a path-free warning) when the index could
        // not be written; the in-memory library keeps working either way.
        public bool Save(LocalLibraryIndex index)
        {
            if (_filePath == null || index == null)
            {
                return false;
            }

            string temp = _filePath + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));

                string json = JsonConvert.SerializeObject(index, SerializerSettings);
                File.WriteAllText(temp, json, new UTF8Encoding(false));

                if (File.Exists(_filePath))
                {
                    File.Replace(temp, _filePath, null);
                }
                else
                {
                    File.Move(temp, _filePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "Local Files: the library index could not be saved. " + UserFacingErrors.ForLog(ex));

                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                    // Best-effort cleanup of the temp file only.
                }

                return false;
            }
        }

        private void TryKeepCorruptCopy()
        {
            try
            {
                string bad = _filePath + ".bad";

                if (File.Exists(bad))
                {
                    File.Delete(bad);
                }

                File.Move(_filePath, bad);
            }
            catch (Exception)
            {
                // Diagnostic convenience only.
            }
        }

        // =========================================================
        // IDENTITY
        // =========================================================
        //
        // Path-free stable id for a file first seen at relativePath with
        // this size: "local-" + 16 hex of SHA-1(lower-case relative path |
        // size). It is assigned once and then CARRIED FORWARD in the index,
        // so later size/timestamp changes (a re-encode in place) keep the
        // id. The relative path - not the absolute one - is hashed so
        // moving the whole library to another drive letter or share (same
        // folder layout) keeps every id. If the same relative path + size
        // exists under two locations, the caller makes the id unique with
        // the suffix argument.
        public static string NewItemId(string relativePath, long sizeBytes, int suffix)
        {
            string key = (relativePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant() +
                         "|" + sizeBytes + (suffix > 0 ? "|" + suffix : string.Empty);

            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var builder = new StringBuilder("local-", 6 + 16);

                for (int i = 0; i < 8; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
