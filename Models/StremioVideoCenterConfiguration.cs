namespace StremioVideoCenter.Models
{
    // Lightweight bundle of the credentials/configuration values the View
    // needs, in place of individually-growing positional constructor
    // parameters now that there are four independent optional values.
    // Deliberately limited to exactly these four existing settings - not a
    // general configuration framework, and not intended to grow beyond what
    // StremioVideoCenterSettings already exposes.
    public class StremioVideoCenterConfiguration
    {
        public string TmdbApiKey { get; set; }

        public string OmdbApiKey { get; set; }

        public string RealDebridApiKey { get; set; }

        // Raw newline-delimited addon URLs, same shape as the underlying
        // settings field. Parsing (trim/blank-skip/dedupe) happens in
        // StremioAddonRegistry.FromConfiguredUrls, not here - this is just a
        // value carrier.
        public string StremioAddonUrls { get; set; }
    }
}
