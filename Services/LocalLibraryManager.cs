using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL LIBRARY MANAGER
    // =============================================================
    //
    // The single owner of the Local Files library at runtime:
    //   * Library Locations (add / remove / persist via the plugin's
    //     existing settings save delegate),
    //   * the in-memory index and its persistence (LocalMediaIndexStore),
    //   * single-flight, cancellable scan -> reconcile -> match
    //     orchestration (LocalMediaScanService / LocalMediaMatchingService),
    //   * user match corrections,
    //   * the LOCAL PLAYBACK SOURCE PROVIDER: resolving a canonical key (or
    //     a library item id) to playable NormalizedPlaybackSources whose
    //     Url is the file's current absolute path.
    //
    // It is one instance for the plugin's lifetime. It has no UI types; the
    // page subscribes to Changed (raised on arbitrary threads - the page
    // marshals to the UI thread).
    //
    // THREADING
    // ---------
    // All index state is guarded by _gate. The UI only ever reads CLONED
    // snapshots (GetItemsSnapshot), never the live items, so a background
    // scan/match can never be observed half-updated. Only one scan runs at a
    // time (StartScan returns false while one is active); library edits
    // (folders, match corrections) are refused while a scan runs so the
    // scan's result can never overwrite them. A cancelled scan changes
    // nothing. The scan state is reset in a finally block, so an exception
    // or cancellation can never leave the manager "busy".
    //
    // PRIVACY
    // -------
    // Paths exist only inside the index file, in memory, and in the Url of
    // a PlaybackSource handed to mpv. Nothing here logs one, puts one in a
    // message, or copies one into settings / saved-source references.
    public sealed class LocalSourceLookup
    {
        public LocalSourceLookup()
        {
            Sources = new List<NormalizedPlaybackSource>();
        }

        // Playable local sources for the requested key, preferred first.
        public List<NormalizedPlaybackSource> Sources { get; private set; }

        // The library has an entry for the key but its file is not there /
        // not readable right now (moved, deleted, drive offline).
        public int UnavailableCount { get; set; }
    }

    public sealed class LocalLibraryManager
    {
        // The addon identity of the built-in Local Files registration
        // (CoreAddonSeedingService) - unchanged.
        public const string AddonProviderKey = "localfiles";

        // Friendly name shown on local sources and in saved-source records.
        public const string AddonDisplayName = "Local Files";

        // Provider-chip key for local sources. NOT a URL and never
        // requested; SourceProviderGrouping only needs a stable string.
        public const string AddonGroupKey = "local-files";

        public const string ItemKeyPrefix = "local-";
        public const string ShowKeyPrefix = "localshow-";

        private const int MissingRetentionDays = 30;
        private const int SaveEveryGroups = 20;

        private readonly Func<StremioVideoCenterSettings> _getSettings;
        private readonly Action _saveSettings;
        private readonly Func<bool> _isEnabled;
        private readonly Func<string> _pickFolder;
        private readonly LocalMediaIndexStore _store;
        private readonly LocalMediaScanService _scanner = new LocalMediaScanService();
        private readonly LocalMediaMatchingService _matching;
        private readonly PlaybackSourceNormalizationService _normalizer = new PlaybackSourceNormalizationService();

        private readonly object _gate = new object();
        private readonly object _saveGate = new object();
        private readonly HashSet<string> _failedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConditionalWeakTable<PlaybackSource, string> _sourceItemIds =
            new ConditionalWeakTable<PlaybackSource, string>();

        private List<LocalLibraryItem> _items = new List<LocalLibraryItem>();
        private bool _loaded;
        private DateTime _lastScanUtc;
        private CancellationTokenSource _scanCts;
        private LocalScanPhase _phase;
        private LocalMediaScanProgress _progress = new LocalMediaScanProgress();
        private string _lastMessage;
        private bool _lastFailed;
        private int _saveVersion;
        private int _lastSavedVersion;
        private long _lastProgressRaiseTicks;

        public LocalLibraryManager(
            Func<StremioVideoCenterSettings> getSettings,
            Action saveSettings,
            string pluginDataDirectory,
            Func<bool> isEnabled,
            Func<string> pickFolder,
            CinemetaService cinemeta,
            KitsuService kitsu)
        {
            if (getSettings == null)
            {
                throw new ArgumentNullException(nameof(getSettings));
            }

            _getSettings = getSettings;
            _saveSettings = saveSettings;
            _isEnabled = isEnabled ?? (() => true);
            _pickFolder = pickFolder;
            _store = new LocalMediaIndexStore(pluginDataDirectory);
            _matching = new LocalMediaMatchingService(cinemeta, kitsu);
        }

        // Raised whenever the library, scan state or folder list changed.
        // May be raised on any thread.
        public event EventHandler Changed;

        // =========================================================
        // KEYS
        // =========================================================

        public static bool IsLocalItemKey(string key)
        {
            return !string.IsNullOrEmpty(key) &&
                   key.StartsWith(ItemKeyPrefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLocalShowKey(string key)
        {
            return !string.IsNullOrEmpty(key) &&
                   key.StartsWith(ShowKeyPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================
        // ENABLED
        // =========================================================
        //
        // Mirrors the Local Files addon instance's Enabled flag (read live).
        // When false the page shows a notice, nothing scans, and
        // FindSourcesAsync returns nothing - no other addon is affected.
        public bool IsEnabled
        {
            get
            {
                try
                {
                    return _isEnabled();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // =========================================================
        // STATE
        // =========================================================

        public bool IsBusy
        {
            get { lock (_gate) { return _scanCts != null; } }
        }

        public LocalLibraryStatus GetStatus()
        {
            lock (_gate)
            {
                return new LocalLibraryStatus
                {
                    Phase = _phase,
                    Progress = new LocalMediaScanProgress
                    {
                        FilesScanned = _progress.FilesScanned,
                        VideoFilesFound = _progress.VideoFilesFound,
                        Phase = _progress.Phase,
                        LocationIndex = _progress.LocationIndex,
                        LocationCount = _progress.LocationCount,
                        MatchesDone = _progress.MatchesDone,
                        MatchesTotal = _progress.MatchesTotal
                    },
                    LastMessage = _lastMessage,
                    LastScanFailed = _lastFailed
                };
            }
        }

        public DateTime LastScanUtc
        {
            get { lock (_gate) { return _lastScanUtc; } }
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;

            if (handler != null)
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    StremioVideoCenterLogger.Warn("Local Files: a change listener failed. " + UserFacingErrors.ForLog(ex));
                }
            }
        }

        // =========================================================
        // LOAD
        // =========================================================

        public Task EnsureLoadedAsync()
        {
            lock (_gate)
            {
                if (_loaded)
                {
                    return Task.FromResult(true);
                }
            }

            return Task.Run(() =>
            {
                LocalLibraryIndex index = _store.Load();

                lock (_gate)
                {
                    if (!_loaded)
                    {
                        _items = index.Items ?? new List<LocalLibraryItem>();
                        _lastScanUtc = index.LastScanUtc;
                        _loaded = true;
                    }
                }

                RaiseChanged();
            });
        }

        public List<LocalLibraryItem> GetItemsSnapshot()
        {
            lock (_gate)
            {
                return _items.Select(i => i.Clone()).ToList();
            }
        }

        public LocalLibraryItem GetItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (_gate)
            {
                LocalLibraryItem item = _items.FirstOrDefault(i =>
                    string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

                return item != null ? item.Clone() : null;
            }
        }

        // =========================================================
        // LIBRARY LOCATIONS
        // =========================================================

        private LocalLibraryService CreateLibraryService()
        {
            StremioVideoCenterSettings settings = _getSettings();

            return settings != null ? new LocalLibraryService(settings) : null;
        }

        public List<LibraryLocation> GetLocations()
        {
            LocalLibraryService service = CreateLibraryService();

            return service != null
                ? service.GetLocations().Select(l => new LibraryLocation
                  {
                      Path = l.Path,
                      DisplayName = l.DisplayName,
                      AddedUtc = l.AddedUtc
                  }).ToList()
                : new List<LibraryLocation>();
        }

        public bool CanPickFolder
        {
            get { return _pickFolder != null; }
        }

        // Shows Playnite's folder picker (must be called on the UI thread).
        // Returns null when cancelled or unavailable.
        public string PickFolder()
        {
            if (_pickFolder == null)
            {
                return null;
            }

            try
            {
                string picked = _pickFolder();

                return string.IsNullOrWhiteSpace(picked) ? null : picked;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: the folder picker failed. " + UserFacingErrors.ForLog(ex));
                return null;
            }
        }

        // Adds a folder and saves the settings. Refused while a scan runs
        // (the scan works from a snapshot of the locations).
        public AddLocationResult AddFolder(string path, out bool refusedBecauseBusy)
        {
            refusedBecauseBusy = false;

            if (IsBusy)
            {
                refusedBecauseBusy = true;
                return AddLocationResult.Invalid;
            }

            LocalLibraryService service = CreateLibraryService();

            if (service == null)
            {
                return AddLocationResult.Invalid;
            }

            AddLocationResult result = service.TryAddLocation(path);

            if (result == AddLocationResult.Added)
            {
                PersistSettings();
                StremioVideoCenterLogger.Info("Local Files: a library folder was added.");
                RaiseChanged();
            }

            return result;
        }

        // Removes a folder, saves the settings and drops its items from the
        // index (a removed folder's files leave the library; their watch
        // state is untouched).
        public bool RemoveFolder(string path, out bool refusedBecauseBusy)
        {
            refusedBecauseBusy = false;

            if (IsBusy)
            {
                refusedBecauseBusy = true;
                return false;
            }

            LocalLibraryService service = CreateLibraryService();

            if (service == null || !service.RemoveLocation(path))
            {
                return false;
            }

            PersistSettings();

            lock (_gate)
            {
                _items = _items
                    .Where(i => !LocalLibraryService.PathsEqual(i.LocationPath, path))
                    .ToList();
                MarkDuplicates(_items);
            }

            SaveIndexAsync();
            StremioVideoCenterLogger.Info("Local Files: a library folder was removed.");
            RaiseChanged();

            return true;
        }

        private void PersistSettings()
        {
            try
            {
                if (_saveSettings != null)
                {
                    _saveSettings();
                }
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: the settings could not be saved. " + UserFacingErrors.ForLog(ex));
            }
        }

        // Folder states are checked on a background thread (a disconnected
        // share can block Directory.Exists for a long time).
        public Task<List<LocalFolderStatus>> GetFolderStatusesAsync()
        {
            LocalLibraryService service = CreateLibraryService();

            if (service == null)
            {
                return Task.FromResult(new List<LocalFolderStatus>());
            }

            Dictionary<string, int> counts;

            lock (_gate)
            {
                counts = _items
                    .Where(i => !i.IsMissing && string.IsNullOrEmpty(i.DuplicateOfId))
                    .GroupBy(i => (i.LocationPath ?? string.Empty).ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            return Task.Run(() => service.GetFolderStatuses(path =>
            {
                int count;
                return counts.TryGetValue((path ?? string.Empty).ToLowerInvariant(), out count) ? count : 0;
            }));
        }

        // =========================================================
        // SCAN
        // =========================================================

        // Starts a scan + match run in the background. Returns false when
        // Local Files is disabled or a run is already active. When
        // retryUnmatched is true, files that were tried and not matched are
        // looked up again (otherwise only new / changed files cost a lookup).
        public bool StartScan(bool retryUnmatched)
        {
            if (!IsEnabled)
            {
                return false;
            }

            List<LibraryLocation> locations = GetLocations();
            CancellationToken token;

            lock (_gate)
            {
                if (_scanCts != null)
                {
                    return false;
                }

                _scanCts = new CancellationTokenSource();
                token = _scanCts.Token;
                _phase = LocalScanPhase.Scanning;
                _progress = new LocalMediaScanProgress { Phase = "Scanning folders", LocationCount = locations.Count };
                _lastMessage = null;
                _lastFailed = false;
            }

            RaiseChanged();

            Task.Run(() => RunScanAsync(locations, retryUnmatched, token));

            return true;
        }

        public void CancelScan()
        {
            CancellationTokenSource cts;

            lock (_gate)
            {
                cts = _scanCts;
            }

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Finished at the same moment; nothing to cancel.
                }
            }
        }

        private async Task RunScanAsync(List<LibraryLocation> locations, bool retryUnmatched, CancellationToken token)
        {
            string message = null;
            bool failed = false;

            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);

                var scans = new List<ScannedLocation>();
                int index = 0;

                foreach (LibraryLocation location in locations)
                {
                    token.ThrowIfCancellationRequested();

                    index++;
                    int locationIndex = index;
                    int locationCount = locations.Count;

                    var progress = new Progress<LocalMediaScanProgress>(p =>
                    {
                        p.LocationIndex = locationIndex;
                        p.LocationCount = locationCount;
                        SetProgress(p, false);
                    });

                    LocalMediaScanResult result = await _scanner.ScanAsync(location, progress, token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();

                    scans.Add(new ScannedLocation { Path = location.Path, Result = result });
                }

                token.ThrowIfCancellationRequested();

                int totalFiles = scans.Sum(s => s.Result.Files.Count);
                int excluded = scans.Sum(s => s.Result.ExcludedCount);
                int unreadable = scans.Sum(s => s.Result.InaccessibleCount);
                int links = scans.Sum(s => s.Result.SkippedLinkCount);
                int offline = scans.Count(s => !s.Result.LocationAccessible);

                DateTime now = DateTime.UtcNow;
                var configured = new HashSet<string>(
                    locations.Select(l => (l.Path ?? string.Empty).ToLowerInvariant()));

                int added;

                lock (_gate)
                {
                    int before = _items.Count;
                    List<LocalLibraryItem> merged = Reconcile(_items, scans, configured, now);
                    MarkDuplicates(merged);
                    added = merged.Count(m => m.FirstSeenUtc >= now);
                    _items = merged;
                    _lastScanUtc = now;
                }

                SaveIndexAsync();
                RaiseChanged();

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "Local Files: scan finished. folders={0} offline={1} videos={2} new={3} excluded={4} unreadable={5} linksSkipped={6}",
                        locations.Count, offline, totalFiles, added, excluded, unreadable, links));

                lock (_gate)
                {
                    _phase = LocalScanPhase.Matching;
                    _progress = new LocalMediaScanProgress { Phase = "Matching titles" };
                }

                RaiseChanged();

                int unmatchedAfter = await MatchAllAsync(retryUnmatched, token).ConfigureAwait(false);

                message = BuildCompletionMessage(totalFiles, locations.Count, offline, excluded, unreadable, unmatchedAfter);
            }
            catch (OperationCanceledException)
            {
                bool matching;

                lock (_gate)
                {
                    matching = _phase == LocalScanPhase.Matching;
                }

                message = matching
                    ? "Matching stopped. Titles matched so far were kept."
                    : "Scan cancelled. The library was not changed.";

                if (matching)
                {
                    SaveIndexAsync();
                }
            }
            catch (Exception ex)
            {
                failed = true;
                message = "The scan failed (" + ex.GetType().Name + "). The library was not changed.";
                StremioVideoCenterLogger.Error("Local Files: the scan failed. " + UserFacingErrors.ForLog(ex));
            }
            finally
            {
                CancellationTokenSource cts;

                lock (_gate)
                {
                    cts = _scanCts;
                    _scanCts = null;
                    _phase = LocalScanPhase.Idle;
                    _lastMessage = message;
                    _lastFailed = failed;
                }

                if (cts != null)
                {
                    cts.Dispose();
                }

                RaiseChanged();
            }
        }

        private static string BuildCompletionMessage(
            int videos, int folders, int offline, int excluded, int unreadable, int unmatched)
        {
            var builder = new StringBuilder();
            builder.Append(videos == 1 ? "1 video" : videos + " videos");
            builder.Append(" in ").Append(folders == 1 ? "1 folder" : folders + " folders");

            if (unmatched > 0)
            {
                builder.Append(", ").Append(unmatched).Append(" not matched");
            }

            if (excluded > 0)
            {
                builder.Append(", ").Append(excluded).Append(" sample/extra file(s) skipped");
            }

            if (offline > 0)
            {
                builder.Append(", ").Append(offline).Append(" folder(s) unavailable");
            }

            if (unreadable > 0)
            {
                builder.Append(", ").Append(unreadable).Append(" item(s) unreadable");
            }

            return builder.Append('.').ToString();
        }

        private void SetProgress(LocalMediaScanProgress p, bool force)
        {
            long now = DateTime.UtcNow.Ticks;

            lock (_gate)
            {
                p.Phase = p.Phase ?? _progress.Phase;
                p.MatchesDone = _progress.MatchesDone;
                p.MatchesTotal = _progress.MatchesTotal;
                _progress = p;

                if (!force && now - _lastProgressRaiseTicks < TimeSpan.TicksPerMillisecond * 150)
                {
                    return;
                }

                _lastProgressRaiseTicks = now;
            }

            RaiseChanged();
        }

        // =========================================================
        // RECONCILE (scan results -> index)
        // =========================================================
        //
        // IDENTITY, RENAMES AND MOVES
        // ---------------------------
        // A file is first recognised by its location + relative path. A
        // file that appears at a NEW path is checked against index entries
        // that were not found in this scan: the same size and last-write
        // time (or the same file name and size) is treated as that file
        // having been renamed or moved, and it KEEPS its Id, its match and
        // its user choices - so its watch progress (keyed by canonical key /
        // Id) follows it. Anything else is a new file with a new Id. An
        // entry whose file is gone is kept flagged IsMissing for 30 days (so
        // a later re-appearance can still re-attach) and then dropped; an
        // entry of an unreachable (offline) location is left exactly as it
        // was. Entries of locations that are no longer configured are
        // dropped.
        internal sealed class ScannedLocation
        {
            public string Path;
            public LocalMediaScanResult Result;
        }

        internal static List<LocalLibraryItem> Reconcile(
            List<LocalLibraryItem> existing,
            List<ScannedLocation> scans,
            HashSet<string> configuredLocations,
            DateTime nowUtc)
        {
            var byKey = new Dictionary<string, LocalLibraryItem>(StringComparer.Ordinal);

            foreach (LocalLibraryItem old in existing)
            {
                string key = PathKey(old.LocationPath, old.RelativePath);

                if (!byKey.ContainsKey(key))
                {
                    byKey[key] = old;
                }
            }

            var claimed = new HashSet<LocalLibraryItem>();
            var result = new List<LocalLibraryItem>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingNew = new List<LocalMediaFile>();
            var scannedOk = new HashSet<string>(StringComparer.Ordinal);

            foreach (ScannedLocation scan in scans)
            {
                if (!scan.Result.LocationAccessible)
                {
                    continue;
                }

                scannedOk.Add((scan.Path ?? string.Empty).ToLowerInvariant());

                foreach (LocalMediaFile file in scan.Result.Files)
                {
                    LocalLibraryItem old;

                    if (byKey.TryGetValue(PathKey(file.LocationPath, file.RelativePath), out old) && !claimed.Contains(old))
                    {
                        claimed.Add(old);
                        LocalLibraryItem updated = UpdateFrom(old, file, nowUtc);
                        usedIds.Add(updated.Id);
                        result.Add(updated);
                    }
                    else
                    {
                        pendingNew.Add(file);
                    }
                }
            }

            // Unclaimed entries of scanned-OK locations are the rename /
            // move candidates (their file was not found where it was).
            List<LocalLibraryItem> movable = existing
                .Where(o => !claimed.Contains(o) &&
                            scannedOk.Contains((o.LocationPath ?? string.Empty).ToLowerInvariant()))
                .ToList();

            foreach (LocalMediaFile file in pendingNew)
            {
                LocalLibraryItem moved = movable.FirstOrDefault(o =>
                                             !claimed.Contains(o) && o.SizeBytes == file.SizeBytes &&
                                             o.LastWriteUtcTicks == file.LastWriteUtcTicks) ??
                                         movable.FirstOrDefault(o =>
                                             !claimed.Contains(o) && o.SizeBytes == file.SizeBytes &&
                                             string.Equals(o.FileName, file.FileName, StringComparison.OrdinalIgnoreCase));

                LocalLibraryItem item;

                if (moved != null)
                {
                    claimed.Add(moved);
                    item = UpdateFrom(moved, file, nowUtc);
                }
                else
                {
                    item = NewItem(file, nowUtc, usedIds);
                }

                usedIds.Add(item.Id);
                result.Add(item);
            }

            foreach (LocalLibraryItem old in existing)
            {
                if (claimed.Contains(old))
                {
                    continue;
                }

                string location = (old.LocationPath ?? string.Empty).ToLowerInvariant();

                if (!configuredLocations.Contains(location))
                {
                    continue;
                }

                LocalLibraryItem keep = old.Clone();

                if (scannedOk.Contains(location))
                {
                    if ((nowUtc - old.LastSeenUtc).TotalDays > MissingRetentionDays)
                    {
                        continue;
                    }

                    keep.IsMissing = true;
                }

                if (usedIds.Add(keep.Id))
                {
                    result.Add(keep);
                }
            }

            return result;
        }

        private static string PathKey(string location, string relative)
        {
            return (location ?? string.Empty).ToLowerInvariant() + "|" + (relative ?? string.Empty).ToLowerInvariant();
        }

        private static LocalLibraryItem UpdateFrom(LocalLibraryItem old, LocalMediaFile file, DateTime nowUtc)
        {
            LocalLibraryItem item = old.Clone();

            item.LocationPath = file.LocationPath;
            item.RelativePath = file.RelativePath;
            item.FileName = file.FileName;
            item.SizeBytes = file.SizeBytes;
            item.LastWriteUtcTicks = file.LastWriteUtcTicks;
            item.Kind = file.Kind;
            item.ParsedTitle = file.ParsedTitle;
            item.ParsedYear = file.ParsedYear;
            item.ParsedSeason = file.ParsedSeason;
            item.ParsedEpisode = file.ParsedEpisode;
            item.ParsedEpisodeEnd = file.ParsedEpisodeEnd;
            item.LooksLikeAnime = file.LooksLikeAnime;
            item.IsAmbiguous = file.IsAmbiguous;
            item.AmbiguityReason = file.AmbiguityReason;
            item.IsMissing = false;
            item.LastSeenUtc = nowUtc;

            // The name now reads as a different title (or as a different
            // kind of thing): the old match no longer applies. An unchanged
            // title keeps its match even if the file was re-encoded in
            // place (size / time changed) - no network request needed.
            string key = MatchQueryKeyFor(item);

            if (!string.Equals(item.MatchQueryKey, key, StringComparison.Ordinal))
            {
                ClearMatch(item);
                item.MatchQueryKey = null;
            }

            if (item.IsAmbiguous && !item.IsMatched)
            {
                item.MatchStatus = LocalMatchStatus.Unmatched;
            }

            return item;
        }

        private static LocalLibraryItem NewItem(LocalMediaFile file, DateTime nowUtc, HashSet<string> usedIds)
        {
            int suffix = 0;
            string id = LocalMediaIndexStore.NewItemId(file.RelativePath, file.SizeBytes, suffix);

            while (usedIds.Contains(id))
            {
                suffix++;
                id = LocalMediaIndexStore.NewItemId(file.RelativePath, file.SizeBytes, suffix);
            }

            var item = new LocalLibraryItem
            {
                Id = id,
                LocationPath = file.LocationPath,
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                SizeBytes = file.SizeBytes,
                LastWriteUtcTicks = file.LastWriteUtcTicks,
                Kind = file.Kind,
                ParsedTitle = file.ParsedTitle,
                ParsedYear = file.ParsedYear,
                ParsedSeason = file.ParsedSeason,
                ParsedEpisode = file.ParsedEpisode,
                ParsedEpisodeEnd = file.ParsedEpisodeEnd,
                LooksLikeAnime = file.LooksLikeAnime,
                IsAmbiguous = file.IsAmbiguous,
                AmbiguityReason = file.AmbiguityReason,
                MatchStatus = LocalMatchStatus.Unmatched,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc
            };

            return item;
        }

        // "kind|anime|normalised title|year" - what a lookup is based on.
        // Null when the file has no usable title.
        internal static string MatchQueryKeyFor(LocalLibraryItem item)
        {
            string title = LocalMediaMatchingService.NormaliseTitle(item.ParsedTitle);

            if (title.Length == 0)
            {
                return null;
            }

            return (item.IsEpisode ? "tv" : "movie") + "|" +
                   (item.LooksLikeAnime ? "anime" : "std") + "|" +
                   title + "|" +
                   (item.ParsedYear.HasValue ? item.ParsedYear.Value.ToString() : string.Empty);
        }

        private static void ClearMatch(LocalLibraryItem item)
        {
            item.MatchStatus = LocalMatchStatus.Unmatched;
            item.MatchedId = null;
            item.MatchedType = null;
            item.MatchedTitle = null;
            item.MatchedYear = null;
            item.PosterUrl = null;
            item.Overview = null;
            item.MatchedSeason = null;
            item.MatchedEpisode = null;
            item.Candidates = new List<LocalMatchCandidate>();
        }

        // Marks every copy after the first as a duplicate of the primary
        // (largest file; ties by first seen then id). Matched items group by
        // canonical key; unmatched ones by their parsed identity.
        internal static void MarkDuplicates(List<LocalLibraryItem> items)
        {
            foreach (LocalLibraryItem item in items)
            {
                item.DuplicateOfId = null;
            }

            IEnumerable<IGrouping<string, LocalLibraryItem>> groups = items
                .Where(i => !i.IsMissing && !i.IsAmbiguous)
                .Select(i => new { Item = i, Key = DuplicateKey(i) })
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key, x => x.Item, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (IGrouping<string, LocalLibraryItem> group in groups.ToList())
            {
                LocalLibraryItem primary = group
                    .OrderByDescending(i => i.SizeBytes)
                    .ThenBy(i => i.FirstSeenUtc)
                    .ThenBy(i => i.Id, StringComparer.Ordinal)
                    .First();

                foreach (LocalLibraryItem other in group.Where(i => !ReferenceEquals(i, primary)))
                {
                    other.DuplicateOfId = primary.Id;
                }
            }
        }

        private static string DuplicateKey(LocalLibraryItem item)
        {
            if (item.IsMatched)
            {
                string canonical = item.CanonicalKey;

                // A matched item whose numbering is unknown plays under its
                // own id: never a duplicate of anything.
                return IsLocalItemKey(canonical) ? null : canonical;
            }

            if (item.MatchQueryKey == null)
            {
                return null;
            }

            if (item.IsEpisode)
            {
                return item.ParsedEpisode.HasValue
                    ? "u|" + item.MatchQueryKey + "|" + (item.ParsedSeason ?? 0) + "|" + item.ParsedEpisode.Value
                    : null;
            }

            return "u|" + item.MatchQueryKey;
        }

        // =========================================================
        // MATCHING
        // =========================================================

        // Returns how many items remain unmatched afterwards.
        private async Task<int> MatchAllAsync(bool retryUnmatched, CancellationToken token)
        {
            List<IGrouping<string, string>> groups;

            lock (_gate)
            {
                groups = _items
                    .Where(i => !i.IsMissing && !i.IsAmbiguous &&
                                i.MatchStatus != LocalMatchStatus.UserConfirmed &&
                                i.MatchStatus != LocalMatchStatus.UserUnmatched &&
                                MatchQueryKeyFor(i) != null &&
                                (i.MatchQueryKey == null ||
                                 (retryUnmatched &&
                                  (i.MatchStatus == LocalMatchStatus.Unmatched ||
                                   i.MatchStatus == LocalMatchStatus.NeedsReview))))
                    .GroupBy(i => MatchQueryKeyFor(i), i => i.Id, StringComparer.Ordinal)
                    .ToList();

                _progress = new LocalMediaScanProgress
                {
                    Phase = "Matching titles",
                    MatchesTotal = groups.Count
                };
            }

            RaiseChanged();

            int done = 0;
            int failedGroups = 0;

            foreach (IGrouping<string, string> group in groups)
            {
                token.ThrowIfCancellationRequested();

                string key = group.Key;
                var ids = new HashSet<string>(group, StringComparer.OrdinalIgnoreCase);
                LocalMatchQuery query = null;
                LocalLibraryItem sibling = null;

                lock (_gate)
                {
                    LocalLibraryItem first = _items.FirstOrDefault(i => ids.Contains(i.Id));

                    if (first != null)
                    {
                        query = new LocalMatchQuery
                        {
                            Title = first.ParsedTitle,
                            Year = first.ParsedYear,
                            IsEpisode = first.IsEpisode,
                            LooksLikeAnime = first.LooksLikeAnime,
                            IsUnknownKind = first.Kind == LocalMediaKind.Unknown
                        };

                        // Already matched under the same key (another file of
                        // this show / movie in the index): reuse, no lookup.
                        sibling = _items.FirstOrDefault(i =>
                            !ids.Contains(i.Id) && i.IsMatched &&
                            string.Equals(i.MatchQueryKey, key, StringComparison.Ordinal));
                    }
                }

                if (query == null)
                {
                    continue;
                }

                LocalMatchOutcome outcome = null;
                LocalMatchCandidate reuse = null;

                if (sibling != null)
                {
                    reuse = new LocalMatchCandidate
                    {
                        Id = sibling.MatchedId,
                        Type = sibling.MatchedType,
                        Title = sibling.MatchedTitle,
                        Year = sibling.MatchedYear,
                        Poster = sibling.PosterUrl
                    };
                }
                else
                {
                    outcome = await _matching.MatchAsync(query, token).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    foreach (LocalLibraryItem item in _items.Where(i => ids.Contains(i.Id)))
                    {
                        if (reuse != null)
                        {
                            ApplyCandidate(item, reuse);
                            item.MatchStatus = sibling.MatchStatus == LocalMatchStatus.UserConfirmed
                                ? LocalMatchStatus.Matched
                                : sibling.MatchStatus;
                            item.MatchQueryKey = key;
                            item.Candidates = new List<LocalMatchCandidate>(sibling.Candidates);
                        }
                        else if (outcome.NetworkFailed)
                        {
                            // Do not record "tried": retry on the next scan.
                            item.MatchQueryKey = null;
                        }
                        else
                        {
                            ClearMatch(item);
                            item.MatchQueryKey = key;
                            item.Candidates = outcome.Candidates.Select(CloneCandidate).ToList();
                            item.MatchStatus = outcome.Status;

                            if (outcome.Status == LocalMatchStatus.Matched && outcome.Accepted != null)
                            {
                                ApplyCandidate(item, outcome.Accepted);
                            }
                        }
                    }

                    if (outcome != null && outcome.NetworkFailed)
                    {
                        failedGroups++;
                    }
                }

                done++;

                lock (_gate)
                {
                    _progress = new LocalMediaScanProgress
                    {
                        Phase = "Matching titles",
                        MatchesDone = done,
                        MatchesTotal = groups.Count
                    };
                }

                RaiseChanged();

                if (done % SaveEveryGroups == 0)
                {
                    lock (_gate)
                    {
                        MarkDuplicates(_items);
                    }

                    SaveIndexAsync();
                }
            }

            int unmatched;

            lock (_gate)
            {
                MarkDuplicates(_items);
                unmatched = _items.Count(i => !i.IsMissing && !i.IsMatched);
            }

            SaveIndexAsync();
            RaiseChanged();

            if (failedGroups > 0)
            {
                StremioVideoCenterLogger.Warn(
                    "Local Files: " + failedGroups + " title lookup(s) failed and will be retried on the next scan.");
            }

            return unmatched;
        }

        private static LocalMatchCandidate CloneCandidate(LocalMatchCandidate c)
        {
            return new LocalMatchCandidate
            {
                Id = c.Id,
                Type = c.Type,
                Title = c.Title,
                Year = c.Year,
                Poster = c.Poster,
                Score = c.Score
            };
        }

        private static void ApplyCandidate(LocalLibraryItem item, LocalMatchCandidate candidate)
        {
            item.MatchedId = candidate.Id;
            item.MatchedType = candidate.Type;
            item.MatchedTitle = candidate.Title;
            item.MatchedYear = candidate.Year;
            item.PosterUrl = candidate.Poster;
            item.MatchStatus = LocalMatchStatus.Matched;

            bool kitsu = candidate.Id != null &&
                         candidate.Id.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase);

            if (item.IsEpisode)
            {
                // Kitsu episodes carry no season (one Kitsu id per season);
                // Cinemeta episodes need both numbers to form tt:S:E.
                item.MatchedSeason = kitsu ? null : item.ParsedSeason;
                item.MatchedEpisode = item.ParsedEpisode;
            }
            else
            {
                item.MatchedSeason = null;
                item.MatchedEpisode = null;
            }
        }

        // =========================================================
        // USER MATCH CORRECTION
        // =========================================================

        public Task<List<LocalMatchCandidate>> SearchCandidatesAsync(
            string text,
            int? year,
            bool forEpisode,
            CancellationToken cancellationToken)
        {
            return _matching.SearchAsync(text, year, !forEpisode, forEpisode, cancellationToken);
        }

        // Applies the user's chosen candidate to the item - and, for an
        // episode, to every episode of the same show (same match key) so one
        // choice fixes the whole series. Refused while a scan runs.
        public bool ApplyUserMatch(string itemId, LocalMatchCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id))
            {
                return false;
            }

            lock (_gate)
            {
                if (_scanCts != null)
                {
                    return false;
                }

                LocalLibraryItem target = _items.FirstOrDefault(i =>
                    string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));

                if (target == null)
                {
                    return false;
                }

                // The candidate must suit the file: episodes take a series /
                // anime, a movie takes a movie.
                bool isSeries = !string.Equals(candidate.Type, "movie", StringComparison.OrdinalIgnoreCase);

                if (target.IsEpisode != isSeries)
                {
                    return false;
                }

                foreach (LocalLibraryItem item in GroupOf(target))
                {
                    ApplyCandidate(item, candidate);
                    item.MatchStatus = LocalMatchStatus.UserConfirmed;
                    item.MatchQueryKey = MatchQueryKeyFor(item);
                }

                MarkDuplicates(_items);
            }

            SaveIndexAsync();
            RaiseChanged();

            return true;
        }

        // The user decided this is not any catalogue title: it stays in the
        // library, playable, and is never auto-matched again.
        public bool KeepUnmatched(string itemId)
        {
            lock (_gate)
            {
                if (_scanCts != null)
                {
                    return false;
                }

                LocalLibraryItem target = _items.FirstOrDefault(i =>
                    string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));

                if (target == null)
                {
                    return false;
                }

                foreach (LocalLibraryItem item in GroupOf(target))
                {
                    ClearMatch(item);
                    item.MatchStatus = LocalMatchStatus.UserUnmatched;
                    item.MatchQueryKey = MatchQueryKeyFor(item);
                }

                MarkDuplicates(_items);
            }

            SaveIndexAsync();
            RaiseChanged();

            return true;
        }

        // Caller holds _gate. A movie corrects alone; an episode corrects
        // together with every other episode of the same show (same show key
        // or same lookup key), so one choice fixes the whole series.
        private List<LocalLibraryItem> GroupOf(LocalLibraryItem target)
        {
            if (!target.IsEpisode)
            {
                return new List<LocalLibraryItem> { target };
            }

            string showKey = LocalLibraryBrowse.ShowKeyFor(target);

            return _items
                .Where(i => i.IsEpisode && !i.IsMissing &&
                            (ReferenceEquals(i, target) ||
                             string.Equals(LocalLibraryBrowse.ShowKeyFor(i), showKey, StringComparison.OrdinalIgnoreCase) ||
                             (target.MatchQueryKey != null &&
                              string.Equals(i.MatchQueryKey, target.MatchQueryKey, StringComparison.Ordinal))))
                .ToList();
        }

        // =========================================================
        // PERSISTENCE
        // =========================================================

        private void SaveIndexAsync()
        {
            LocalLibraryIndex snapshot;
            int version;

            lock (_gate)
            {
                snapshot = new LocalLibraryIndex
                {
                    LastScanUtc = _lastScanUtc,
                    Items = _items.Select(i => i.Clone()).ToList()
                };

                version = ++_saveVersion;
            }

            Task.Run(() =>
            {
                lock (_saveGate)
                {
                    // A newer snapshot has already been written.
                    if (version < _lastSavedVersion)
                    {
                        return;
                    }

                    if (_store.Save(snapshot))
                    {
                        _lastSavedVersion = version;
                    }
                }
            });
        }

        // =========================================================
        // LOCAL PLAYBACK SOURCE PROVIDER
        // =========================================================
        //
        // Resolves a canonical key (movie "tt…", episode "tt…:S:E", Kitsu
        // "kitsu:id:ep", or an unmatched file's own "local-…" id) to the
        // playable local files for it, as synthetic NormalizedPlaybackSources
        // of kind Local whose Url is the file's CURRENT absolute path - read
        // from the index at the moment of use, never stored in a saved-source
        // reference. File existence is checked on a background thread.
        //
        // preferredItemId (the card the user picked) is placed first. Items
        // that already failed to play this session are left out so a bad file
        // is not offered again. Disabled Local Files returns nothing.
        public async Task<LocalSourceLookup> FindSourcesAsync(string canonicalKey, string preferredItemId)
        {
            var lookup = new LocalSourceLookup();

            if (!IsEnabled || string.IsNullOrWhiteSpace(canonicalKey))
            {
                return lookup;
            }

            await EnsureLoadedAsync().ConfigureAwait(false);

            List<LocalLibraryItem> candidates;

            lock (_gate)
            {
                candidates = _items
                    .Where(i => !i.IsMissing && !_failedThisSession.Contains(i.Id) && Covers(i, canonicalKey))
                    .Select(i => i.Clone())
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                return lookup;
            }

            candidates = candidates
                .OrderByDescending(i => string.Equals(i.Id, preferredItemId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(i => i.SizeBytes)
                .ToList();

            List<LocalLibraryItem> present = await Task.Run(() =>
            {
                var found = new List<LocalLibraryItem>();

                foreach (LocalLibraryItem item in candidates)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
                        {
                            found.Add(item);
                        }
                    }
                    catch (Exception)
                    {
                        // Treated as unavailable.
                    }
                }

                return found;
            }).ConfigureAwait(false);

            lookup.UnavailableCount = candidates.Count - present.Count;

            foreach (LocalLibraryItem item in present)
            {
                lookup.Sources.Add(BuildSource(item));
            }

            return lookup;
        }

        // Whether the item plays under the given canonical key (its own, its
        // own id, or - for a multi-episode file - any episode it covers).
        internal static bool Covers(LocalLibraryItem item, string canonicalKey)
        {
            if (string.Equals(item.Id, canonicalKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!item.IsMatched)
            {
                return false;
            }

            if (string.Equals(item.CanonicalKey, canonicalKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (item.IsEpisode && item.ParsedEpisodeEnd.HasValue && item.ParsedEpisode.HasValue &&
                item.MatchedEpisode.HasValue)
            {
                int span = Math.Min(item.ParsedEpisodeEnd.Value - item.ParsedEpisode.Value, 50);

                for (int offset = 1; offset <= span; offset++)
                {
                    string covered = item.IsKitsu
                        ? item.MatchedId + ":" + (item.MatchedEpisode.Value + offset)
                        : (item.MatchedSeason.HasValue
                            ? item.MatchedId + ":" + item.MatchedSeason.Value + ":" + (item.MatchedEpisode.Value + offset)
                            : null);

                    if (covered != null && string.Equals(covered, canonicalKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private NormalizedPlaybackSource BuildSource(LocalLibraryItem item)
        {
            var stream = new StremioStream
            {
                Name = "Local file",
                Title = item.FileName,
                SourceAddonName = AddonDisplayName,
                SourceAddonUrl = AddonGroupKey,
                BehaviorHints = new StremioStreamBehaviorHints
                {
                    Filename = item.FileName,
                    VideoSize = item.SizeBytes > 0 ? item.SizeBytes : (long?)null
                }
            };

            var source = new PlaybackSource
            {
                // The actual absolute path: mpv loads local files directly,
                // no file:// conversion.
                Url = item.FullPath,
                Title = item.FileName,
                Name = "Local file",
                SourceAddonUrl = AddonGroupKey,
                SourceAddonName = AddonDisplayName,
                Kind = PlaybackSourceKind.Local,
                OriginatingStream = stream
            };

            try
            {
                _sourceItemIds.Add(source, item.Id);
            }
            catch (ArgumentException)
            {
                // Already registered (cannot happen for a new instance).
            }

            return _normalizer.Normalize(source);
        }

        // The library item id behind a local source, or null.
        public string GetItemId(NormalizedPlaybackSource source)
        {
            string id;

            return source != null && source.Source != null &&
                   _sourceItemIds.TryGetValue(source.Source, out id)
                ? id
                : null;
        }

        // A local file that mpv could not play is left out of this
        // session's local sources (the index entry is untouched).
        public void NotePlaybackFailed(NormalizedPlaybackSource source)
        {
            string id = GetItemId(source);

            if (id != null)
            {
                lock (_gate)
                {
                    _failedThisSession.Add(id);
                }
            }
        }

        // =========================================================
        // MEDIA ITEMS / EPISODE LIST
        // =========================================================

        // The MediaItem that plays this library item through the existing
        // pipeline. Matched items take the catalogue identity (so Movie
        // Details / Episode Browser / Watch State / History see the same
        // title a streaming copy would); unmatched ones carry their
        // path-free local id.
        public MediaItem BuildMediaItem(LocalLibraryItem item)
        {
            if (item == null)
            {
                return null;
            }

            bool matched = item.IsMatched;
            string showTitle = matched ? item.MatchedTitle : item.ParsedTitle;

            if (string.IsNullOrWhiteSpace(showTitle))
            {
                showTitle = Path.GetFileNameWithoutExtension(item.FileName);
            }

            var media = new MediaItem
            {
                LocalKey = item.Id,
                PosterPath = item.PosterUrl,
                Year = matched ? item.MatchedYear : (item.ParsedYear.HasValue ? item.ParsedYear.Value.ToString() : null),
                Type = item.IsEpisode
                    ? (item.IsKitsu || string.Equals(item.MatchedType, "anime", StringComparison.OrdinalIgnoreCase) ? "Anime" : "TV")
                    : "Movie"
            };

            if (!item.IsEpisode)
            {
                media.Title = showTitle;
                media.StremioId = (matched && string.Equals(item.MatchedType, "movie", StringComparison.OrdinalIgnoreCase))
                    ? item.MatchedId
                    : item.Id;
                return media;
            }

            string code = LocalLibraryBrowse.EpisodeCode(item);
            bool numberingKnown = matched &&
                                  item.MatchedEpisode.HasValue &&
                                  (item.IsKitsu || item.MatchedSeason.HasValue);

            media.Title = showTitle + " - " + code;
            media.Subtitle = code;
            media.StremioId = LocalLibraryBrowse.ShowKeyFor(item);

            if (numberingKnown)
            {
                media.Season = item.IsKitsu ? 1 : item.MatchedSeason;
                media.Episode = item.MatchedEpisode;
            }
            else
            {
                media.Season = item.ParsedSeason ?? 1;
                media.Episode = item.ParsedEpisode;
            }

            return media;
        }

        // A catalogue-shaped MediaItem (matched movie / show) for the
        // existing Movie Details / Episode Browser, or null when the item
        // has no catalogue identity.
        public MediaItem BuildCatalogueItem(LocalLibraryItem item)
        {
            if (item == null || !item.IsMatched)
            {
                return null;
            }

            bool anime = item.IsKitsu || string.Equals(item.MatchedType, "anime", StringComparison.OrdinalIgnoreCase);
            bool movie = string.Equals(item.MatchedType, "movie", StringComparison.OrdinalIgnoreCase);

            return new MediaItem
            {
                StremioId = item.MatchedId,
                Title = item.MatchedTitle,
                PosterPath = item.PosterUrl,
                Year = item.MatchedYear,
                Type = movie ? "Movie" : (anime ? "Anime" : "TV")
            };
        }

        // The episode order for one series, built from the library alone -
        // lets Previous / Next and auto-next work with no network. Used for
        // an UNMATCHED local series (showKey "localshow-...", always) and
        // as an offline fallback for a MATCHED series (showKey = its
        // Cinemeta / Kitsu id) when the online episode list cannot be
        // loaded. Episode identity is the file's canonical key, the same one
        // Watch State uses. Null when fewer than two episodes exist
        // (nothing to navigate).
        public EpisodeList BuildLocalEpisodeList(string showKey)
        {
            if (string.IsNullOrWhiteSpace(showKey))
            {
                return null;
            }

            var refs = new List<EpisodeRef>();

            lock (_gate)
            {
                foreach (LocalLibraryItem i in _items)
                {
                    if (!i.IsEpisode || i.IsMissing || !string.IsNullOrEmpty(i.DuplicateOfId) ||
                        _failedThisSession.Contains(i.Id) ||
                        !string.Equals(LocalLibraryBrowse.ShowKeyFor(i), showKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (i.IsMatched)
                    {
                        // Only files whose catalogue numbering is known can
                        // be placed in a matched series' order.
                        if (!i.MatchedEpisode.HasValue || (!i.IsKitsu && !i.MatchedSeason.HasValue))
                        {
                            continue;
                        }

                        refs.Add(new EpisodeRef
                        {
                            CanonicalKey = i.CanonicalKey,
                            ShowStremioId = showKey,
                            Season = i.IsKitsu ? 1 : i.MatchedSeason.Value,
                            Episode = i.MatchedEpisode.Value,
                            IsKitsu = i.IsKitsu
                        });
                    }
                    else if (i.ParsedEpisode.HasValue)
                    {
                        refs.Add(new EpisodeRef
                        {
                            CanonicalKey = i.CanonicalKey,
                            ShowStremioId = showKey,
                            Season = i.ParsedSeason ?? 1,
                            Episode = i.ParsedEpisode.Value,
                            IsKitsu = false
                        });
                    }
                }
            }

            return refs.Count > 1 ? EpisodeList.Create(showKey, refs) : null;
        }
    }
}
