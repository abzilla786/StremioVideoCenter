using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =========================================================
    // OMDB SERVICE
    // =========================================================
    //
    // Handles communication between Stremio Video Center
    // and the Open Movie Database API.
    //
    // The user's OMDb API key is supplied through the
    // plugin settings and is never hard-coded.
    //
    public class OmdbService
    {
        private const string BaseUrl =
            "https://www.omdbapi.com/";

        private readonly string apiKey;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public OmdbService(string apiKey)
        {
            this.apiKey = apiKey;
        }

        // =========================================================
        // TEST CONNECTION
        // =========================================================
        //
        // Performs a simple authenticated OMDb request.
        //
        // Inception (2010) is used only as a harmless API
        // connectivity test.
        //
        public async Task<bool> TestConnectionAsync()
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            using (var client = new HttpClient())
            {
                client.Timeout =
                    TimeSpan.FromSeconds(10);

                try
                {
                    string requestUrl =
                        BaseUrl +
                        "?apikey=" +
                        Uri.EscapeDataString(apiKey.Trim()) +
                        "&t=Inception" +
                        "&y=2010";

                    using (var response =
                        await client.GetAsync(requestUrl))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return false;
                        }

                        string responseContent =
                            await response.Content.ReadAsStringAsync();

                        // OMDb returns Response=True when the
                        // request and supplied API key are valid.
                        return responseContent.Contains(
                            "\"Response\":\"True\"");
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