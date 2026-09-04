using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =========================================================
    // TMDB SERVICE
    // =========================================================
    //
    // Handles communication between Stremio Video Center
    // and The Movie Database API.
    //
    // The user's TMDB API Read Access Token is supplied
    // through the plugin settings and is never hard-coded.
    //
    public class TmdbService
    {
        private const string BaseUrl =
            "https://api.themoviedb.org/3/";

        private readonly string accessToken;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public TmdbService(string accessToken)
        {
            this.accessToken = accessToken;
        }

        // =========================================================
        // TEST CONNECTION
        // =========================================================
        //
        // Performs a simple authenticated TMDB request.
        //
        // Movie 550 is used only as a harmless API connectivity
        // test. We are not using this movie anywhere in the
        // Video Center.
        //
        public async Task<bool> TestConnectionAsync()
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return false;
            }

            using (var client = new HttpClient())
            {
                client.BaseAddress =
                    new Uri(BaseUrl);

                client.Timeout =
                    TimeSpan.FromSeconds(10);

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        accessToken.Trim());

                try
                {
                    using (var response =
                        await client.GetAsync("movie/550"))
                    {
                        return response.IsSuccessStatusCode;
                    }
                }
                catch
                {
                    // Connection failures are handled by the
                    // caller so the plugin itself does not crash.
                    return false;
                }
            }
        }
    }
}