using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // Lightweight bundle of the credentials/configuration values the View
    // needs, in place of individually-growing positional constructor
    // parameters now that there are five independent optional values.
    // Deliberately limited to what StremioVideoCenterSettings already
    // exposes - not a general configuration framework.
    public class StremioVideoCenterConfiguration
    {
        public string TmdbApiKey { get; set; }

        public string OmdbApiKey { get; set; }

        public string RealDebridApiKey { get; set; }

        // Raw newline-delimited addon URLs, same shape as the underlying
        // settings field. Parsing (trim/blank-skip/dedupe) happens in
        // StremioAddonRegistry.FromConfiguredUrls, not here - this is just a
        // value carrier. Kept for backward compatibility alongside
        // AddonInstances below.
        public string StremioAddonUrls { get; set; }

        // Structured addon instances (see Models.AddonInstance). The live
        // discovery path (StremioVideoCenterView.PlaySelectedMediaAsync)
        // builds its registry from this via
        // StremioAddonRegistry.FromAddonInstances - StremioAddonUrls above
        // is carried through only for the Settings UI's legacy textbox.
        public List<AddonInstance> AddonInstances { get; set; }

        // Slice 15 (Local Media architecture foundation): a read-only
        // snapshot of the configured Local Media library locations, so
        // LocalMediaView can honestly display how many are configured
        // without the View needing direct access to the live
        // StremioVideoCenterSettings/plugin save path. Adding/removing
        // locations is not wired into the View this slice - see
        // LocalMediaView/LocalLibraryService remarks.
        public List<LibraryLocation> LibraryLocations { get; set; }
    }
}
