using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL MEDIA MATCHING SERVICE
    // =============================================================
    //
    // Turns a parsed title / year / kind into a Cinemeta (movies, TV) or
    // Kitsu (anime) identity, using ONLY the existing CinemetaService and
    // KitsuService - no new metadata dependency. Queries are made per
    // SHOW (or per movie), never per episode file; the library manager
    // groups files first.
    //
    // RANKING
    // -------
    // Each candidate gets a title similarity (exact normalised match 1.0,
    // article/subtitle-insensitive 0.95, prefix 0.7, otherwise a
    // bigram similarity capped at 0.85) plus a year adjustment (same year
    // +0.25, one year off +0.10, further off -0.35, no year in the query 0).
    //
    // ACCEPTANCE (never a silent guess)
    // ---------------------------------
    // A candidate is accepted automatically only when its title is an
    // (almost) exact match, its year is compatible, and no other candidate
    // is within 0.15 of its score. Anything plausible but not clear-cut is
    // NeedsReview (candidates kept for the user to pick); anything else is
    // Unmatched. A network failure is reported as such (NetworkFailed) so
    // the caller does not record "tried and found nothing".
    public sealed class LocalMatchQuery
    {
        public string Title { get; set; }

        public int? Year { get; set; }

        public bool IsEpisode { get; set; }

        public bool LooksLikeAnime { get; set; }

        // No year and no episode marker: the file might not be a movie at
        // all, so the acceptance rule is the strict one.
        public bool IsUnknownKind { get; set; }
    }

    public sealed class LocalMatchOutcome
    {
        public LocalMatchOutcome()
        {
            Candidates = new List<LocalMatchCandidate>();
        }

        public LocalMatchStatus Status { get; set; }

        public LocalMatchCandidate Accepted { get; set; }

        public List<LocalMatchCandidate> Candidates { get; set; }

        public bool NetworkFailed { get; set; }
    }

    public sealed class LocalMediaMatchingService
    {
        private const int RequestTimeoutMs = 15000;
        private const int MaxCandidatesKept = 5;
        private const double ReviewThreshold = 0.6;
        private const double TieMargin = 0.15;

        private readonly CinemetaService _cinemeta;
        private readonly KitsuService _kitsu;

        public LocalMediaMatchingService(CinemetaService cinemeta, KitsuService kitsu)
        {
            _cinemeta = cinemeta;
            _kitsu = kitsu;
        }

        // =========================================================
        // AUTOMATIC MATCH
        // =========================================================

        public async Task<LocalMatchOutcome> MatchAsync(LocalMatchQuery query, CancellationToken cancellationToken)
        {
            var outcome = new LocalMatchOutcome { Status = LocalMatchStatus.Unmatched };

            if (query == null || string.IsNullOrWhiteSpace(query.Title))
            {
                return outcome;
            }

            string normalised = NormaliseTitle(query.Title);

            if (normalised.Length == 0)
            {
                return outcome;
            }

            bool failed = false;
            List<LocalMatchCandidate> primary;

            if (query.LooksLikeAnime)
            {
                primary = await SearchKitsuAsync(query.Title, cancellationToken).ConfigureAwait(false);
                failed |= primary == null;
            }
            else
            {
                primary = await SearchCinemetaAsync(query.IsEpisode ? "series" : "movie", query.Title, cancellationToken)
                    .ConfigureAwait(false);
                failed |= primary == null;
            }

            List<LocalMatchCandidate> ranked = Rank(query, primary ?? new List<LocalMatchCandidate>());

            LocalMatchCandidate accepted = TryAccept(query, ranked);

            if (accepted != null)
            {
                outcome.Status = LocalMatchStatus.Matched;
                outcome.Accepted = accepted;
                outcome.Candidates = ranked.Take(MaxCandidatesKept).ToList();
                return outcome;
            }

            // Anime with nothing usable from Kitsu: offer Cinemeta series
            // as REVIEW candidates only (never auto-accepted).
            if (query.LooksLikeAnime && !ranked.Any(c => c.Score >= ReviewThreshold))
            {
                List<LocalMatchCandidate> fallback =
                    await SearchCinemetaAsync("series", query.Title, cancellationToken).ConfigureAwait(false);

                failed |= fallback == null;

                if (fallback != null)
                {
                    ranked = Rank(query, ranked.Concat(fallback).ToList());
                }
            }

            outcome.Candidates = ranked.Where(c => c.Score >= ReviewThreshold).Take(MaxCandidatesKept).ToList();
            outcome.Status = outcome.Candidates.Count > 0 ? LocalMatchStatus.NeedsReview : LocalMatchStatus.Unmatched;
            outcome.NetworkFailed = failed && outcome.Candidates.Count == 0;

            return outcome;
        }

        // =========================================================
        // MANUAL SEARCH (the "fix match" flow)
        // =========================================================
        //
        // Searches movies, series and anime for free text and returns the
        // ranked candidates for the user to choose from. Returns null when
        // every provider failed (so the UI can say the lookup failed
        // rather than "no results").
        public async Task<List<LocalMatchCandidate>> SearchAsync(
            string text,
            int? year,
            bool includeMovies,
            bool includeSeries,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<LocalMatchCandidate>();
            }

            var all = new List<LocalMatchCandidate>();
            int succeeded = 0;
            int attempted = 0;

            if (includeMovies)
            {
                attempted++;
                List<LocalMatchCandidate> movies = await SearchCinemetaAsync("movie", text, cancellationToken).ConfigureAwait(false);

                if (movies != null)
                {
                    succeeded++;
                    all.AddRange(movies);
                }
            }

            if (includeSeries)
            {
                attempted++;
                List<LocalMatchCandidate> series = await SearchCinemetaAsync("series", text, cancellationToken).ConfigureAwait(false);

                if (series != null)
                {
                    succeeded++;
                    all.AddRange(series);
                }

                attempted++;
                List<LocalMatchCandidate> anime = await SearchKitsuAsync(text, cancellationToken).ConfigureAwait(false);

                if (anime != null)
                {
                    succeeded++;
                    all.AddRange(anime);
                }
            }

            if (attempted > 0 && succeeded == 0)
            {
                return null;
            }

            var query = new LocalMatchQuery { Title = text, Year = year };

            return Rank(query, all).Take(10).ToList();
        }

        // =========================================================
        // PROVIDERS
        // =========================================================

        private async Task<List<LocalMatchCandidate>> SearchCinemetaAsync(
            string type,
            string title,
            CancellationToken cancellationToken)
        {
            if (_cinemeta == null)
            {
                return null;
            }

            try
            {
                List<CinemetaCatalogItem> items = await Bounded(
                    _cinemeta.SearchCatalogAsync(type, title), cancellationToken).ConfigureAwait(false);

                var list = new List<LocalMatchCandidate>();

                foreach (CinemetaCatalogItem item in items ?? new List<CinemetaCatalogItem>())
                {
                    if (item == null)
                    {
                        continue;
                    }

                    string id = !string.IsNullOrWhiteSpace(item.ImdbId) ? item.ImdbId : item.Id;

                    if (string.IsNullOrWhiteSpace(id) ||
                        !id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(item.Name))
                    {
                        continue;
                    }

                    list.Add(new LocalMatchCandidate
                    {
                        Id = id,
                        Type = type,
                        Title = item.Name.Trim(),
                        Year = FirstYear(item.Year, item.ReleaseInfo, item.Released),
                        Poster = item.Poster
                    });
                }

                return list;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: a Cinemeta title lookup failed. " + UserFacingErrors.ForLog(ex));
                return null;
            }
        }

        private async Task<List<LocalMatchCandidate>> SearchKitsuAsync(string title, CancellationToken cancellationToken)
        {
            if (_kitsu == null)
            {
                return null;
            }

            try
            {
                List<KitsuAnimeResource> items = await Bounded(
                    _kitsu.SearchAnimeOrThrowAsync(title, 0, 10), cancellationToken).ConfigureAwait(false);

                var list = new List<LocalMatchCandidate>();

                foreach (KitsuAnimeResource item in items ?? new List<KitsuAnimeResource>())
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.Attributes == null)
                    {
                        continue;
                    }

                    KitsuAnimeAttributes a = item.Attributes;
                    string display = FirstNonEmpty(a.CanonicalTitle, a.Titles != null ? a.Titles.En : null,
                        a.Titles != null ? a.Titles.EnJp : null, a.Titles != null ? a.Titles.JaJp : null);

                    if (display == null)
                    {
                        continue;
                    }

                    var candidate = new LocalMatchCandidate
                    {
                        Id = "kitsu:" + item.Id.Trim(),
                        Type = "anime",
                        Title = display,
                        Year = FirstYear(a.StartDate),
                        Poster = a.PosterImage != null
                            ? FirstNonEmpty(a.PosterImage.Medium, a.PosterImage.Small, a.PosterImage.Large, a.PosterImage.Original)
                            : null
                    };

                    // Alternate titles are used for scoring only.
                    candidate.AlternateTitles = new[]
                    {
                        a.CanonicalTitle,
                        a.Titles != null ? a.Titles.En : null,
                        a.Titles != null ? a.Titles.EnJp : null,
                        a.Titles != null ? a.Titles.JaJp : null
                    };

                    list.Add(candidate);
                }

                return list;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: a Kitsu title lookup failed. " + UserFacingErrors.ForLog(ex));
                return null;
            }
        }

        // Network wait that honours the caller's cancellation and a hard
        // timeout. The underlying request has its own deadline; this only
        // stops US waiting.
        private static async Task<T> Bounded<T>(Task<T> task, CancellationToken cancellationToken)
        {
            Task delay = Task.Delay(RequestTimeoutMs, cancellationToken);
            Task finished = await Task.WhenAny(task, delay).ConfigureAwait(false);

            if (finished != task)
            {
                // Abandon the request but observe its eventual fault so it
                // never surfaces as an unobserved exception.
                task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The metadata lookup timed out.");
            }

            return await task.ConfigureAwait(false);
        }

        // =========================================================
        // RANKING / ACCEPTANCE
        // =========================================================

        internal static List<LocalMatchCandidate> Rank(LocalMatchQuery query, IEnumerable<LocalMatchCandidate> candidates)
        {
            string wanted = NormaliseTitle(query.Title);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ranked = new List<LocalMatchCandidate>();

            foreach (LocalMatchCandidate candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id) || !seen.Add(candidate.Id))
                {
                    continue;
                }

                double title = TitleSimilarity(wanted, candidate.Title);

                string[] alternates = candidate.AlternateTitles;

                if (alternates != null)
                {
                    foreach (string alternate in alternates)
                    {
                        if (!string.IsNullOrWhiteSpace(alternate))
                        {
                            title = Math.Max(title, TitleSimilarity(wanted, alternate));
                        }
                    }
                }

                candidate.TitleScore = title;
                candidate.Score = Math.Max(0, title + YearAdjustment(query.Year, candidate.Year));
                ranked.Add(candidate);
            }

            // Stable order: score desc, then the provider's own order.
            return ranked
                .Select((c, i) => new { c, i })
                .OrderByDescending(x => x.c.Score)
                .ThenBy(x => x.i)
                .Select(x => x.c)
                .ToList();
        }

        internal static LocalMatchCandidate TryAccept(LocalMatchQuery query, IList<LocalMatchCandidate> ranked)
        {
            if (ranked == null || ranked.Count == 0)
            {
                return null;
            }

            LocalMatchCandidate best = ranked[0];

            // The TITLE must be (almost) exact on its own - a fuzzy title
            // plus a matching year is never enough. Unknown kind (no year,
            // no episode marker) requires a fully exact title.
            if (best.TitleScore < (query.IsUnknownKind ? 1.0 : 0.95))
            {
                return null;
            }

            // The combined score must stay high: a year more than one year
            // off pulls an exact title down to 0.65.
            if (best.Score < 0.95)
            {
                return null;
            }

            // Another candidate within the tie margin means there is no
            // single clear winner.
            if (ranked.Count > 1 && best.Score - ranked[1].Score < TieMargin)
            {
                return null;
            }

            return best;
        }

        internal static double TitleSimilarity(string wantedNormalised, string candidateTitle)
        {
            string candidate = NormaliseTitle(candidateTitle);

            if (wantedNormalised.Length == 0 || candidate.Length == 0)
            {
                return 0;
            }

            if (string.Equals(wantedNormalised, candidate, StringComparison.Ordinal))
            {
                return 1.0;
            }

            if (candidate.StartsWith(wantedNormalised + " ", StringComparison.Ordinal) ||
                wantedNormalised.StartsWith(candidate + " ", StringComparison.Ordinal))
            {
                return 0.7;
            }

            return Math.Min(0.85, BigramSimilarity(wantedNormalised, candidate));
        }

        internal static double YearAdjustment(int? wantedYear, string candidateYear)
        {
            if (!wantedYear.HasValue)
            {
                return 0;
            }

            int year;

            if (string.IsNullOrWhiteSpace(candidateYear) || !int.TryParse(candidateYear, out year))
            {
                return 0;
            }

            int difference = Math.Abs(year - wantedYear.Value);

            if (difference == 0)
            {
                return 0.25;
            }

            if (difference == 1)
            {
                return 0.10;
            }

            return -0.35;
        }

        private static double BigramSimilarity(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2)
            {
                return 0;
            }

            var bigramsA = new Dictionary<string, int>();

            for (int i = 0; i < a.Length - 1; i++)
            {
                string gram = a.Substring(i, 2);
                int count;
                bigramsA[gram] = bigramsA.TryGetValue(gram, out count) ? count + 1 : 1;
            }

            int intersection = 0;

            for (int i = 0; i < b.Length - 1; i++)
            {
                string gram = b.Substring(i, 2);
                int count;

                if (bigramsA.TryGetValue(gram, out count) && count > 0)
                {
                    bigramsA[gram] = count - 1;
                    intersection++;
                }
            }

            return (2.0 * intersection) / ((a.Length - 1) + (b.Length - 1));
        }

        // Lower-case, diacritics removed, "&" -> "and", punctuation -> space,
        // single spaces, leading article dropped ("the", "a", "an").
        public static string NormaliseTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            string decomposed = title.Replace("&", " and ").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (char c in decomposed)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);

                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            string[] words = builder.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length > 1 && (words[0] == "the" || words[0] == "a" || words[0] == "an"))
            {
                words = words.Skip(1).ToArray();
            }

            return string.Join(" ", words);
        }

        private static string FirstYear(params string[] texts)
        {
            foreach (string text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                for (int i = 0; i + 4 <= text.Length; i++)
                {
                    if (char.IsDigit(text[i]) && char.IsDigit(text[i + 1]) &&
                        char.IsDigit(text[i + 2]) && char.IsDigit(text[i + 3]) &&
                        (text[i] == '1' || text[i] == '2') &&
                        (i == 0 || !char.IsDigit(text[i - 1])))
                    {
                        return text.Substring(i, 4);
                    }
                }
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        // =========================================================
        // SELF-CHECKS
        // =========================================================
        public static List<string> RunSelfChecks()
        {
            var results = new List<string>();

            results.Add(RunCheck("Exact title + same year is accepted", () =>
            {
                var q = new LocalMatchQuery { Title = "Inception", Year = 2010 };
                var c = Rank(q, new[]
                {
                    new LocalMatchCandidate { Id = "tt1", Title = "Inception", Year = "2010" },
                    new LocalMatchCandidate { Id = "tt2", Title = "Inception: The Cobol Job", Year = "2010" }
                });
                LocalMatchCandidate accepted = TryAccept(q, c);
                return accepted != null && accepted.Id == "tt1";
            }));

            results.Add(RunCheck("Two exact titles and no year is NOT accepted (needs review)", () =>
            {
                var q = new LocalMatchQuery { Title = "Home" };
                var c = Rank(q, new[]
                {
                    new LocalMatchCandidate { Id = "tt1", Title = "Home", Year = "2015" },
                    new LocalMatchCandidate { Id = "tt2", Title = "Home", Year = "2008" }
                });
                return TryAccept(q, c) == null;
            }));

            results.Add(RunCheck("The year breaks the tie between same-named titles", () =>
            {
                var q = new LocalMatchQuery { Title = "Home", Year = 2015 };
                var c = Rank(q, new[]
                {
                    new LocalMatchCandidate { Id = "tt1", Title = "Home", Year = "2008" },
                    new LocalMatchCandidate { Id = "tt2", Title = "Home", Year = "2015" }
                });
                LocalMatchCandidate accepted = TryAccept(q, c);
                return accepted != null && accepted.Id == "tt2";
            }));

            results.Add(RunCheck("A wrong-year exact title is not accepted", () =>
            {
                var q = new LocalMatchQuery { Title = "Inception", Year = 1995 };
                var c = Rank(q, new[] { new LocalMatchCandidate { Id = "tt1", Title = "Inception", Year = "2010" } });
                return TryAccept(q, c) == null;
            }));

            results.Add(RunCheck("Leading article, case and punctuation do not matter", () =>
            {
                return NormaliseTitle("The Office") == NormaliseTitle("office") &&
                       NormaliseTitle("Spider-Man: Homecoming") == NormaliseTitle("spider man homecoming") &&
                       NormaliseTitle("Tom & Jerry") == NormaliseTitle("Tom and Jerry");
            }));

            results.Add(RunCheck("Unknown-kind match requires an exact title", () =>
            {
                var q = new LocalMatchQuery { Title = "Alien", IsUnknownKind = true };
                var c = Rank(q, new[] { new LocalMatchCandidate { Id = "tt1", Title = "Alien: Romulus", Year = "2024" } });
                return TryAccept(q, c) == null;
            }));

            return results;
        }

        private static string RunCheck(string description, Func<bool> check)
        {
            try
            {
                return (check() ? "PASS - " : "FAIL - ") + description;
            }
            catch (Exception ex)
            {
                return "ERROR - " + description + " (" + ex.GetType().Name + ")";
            }
        }
    }
}
