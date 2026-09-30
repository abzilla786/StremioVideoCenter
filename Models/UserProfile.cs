using System;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // USER PROFILE (Slice 16A - Profile Foundation)
    // =============================================================
    //
    // A local "who's watching" profile - conceptually similar to a
    // Netflix profile. This is explicitly NOT an authentication/security
    // account: there is no password, no PIN, nothing that restricts
    // access to a profile once Zino itself is open. Its only job is to
    // give personal media state (Watch State/Continue Watching/History/
    // My List - Slice 16B onward) a stable owner to key off, per the
    // Slice 16 architecture audit's "a profile owns all personal media
    // state" rule.
    //
    // Id is a Guid generated once at creation and never changes -
    // ProfileService.RenameProfile only ever changes Name, so renaming a
    // profile can never orphan data keyed by Id (see ProfileService
    // remarks). Persisted as part of StremioVideoCenterSettings.Profiles,
    // reusing Playnite's own existing plugin-settings persistence
    // mechanism - the same one AddonInstances/LibraryLocations already
    // use - rather than a new file/database.
    public class UserProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        // A small built-in colour identifier for the profile's avatar
        // tile (e.g. "#E91E63"). No image upload pipeline exists in this
        // codebase and building one is explicitly out of scope for
        // Slice 16 - a fixed palette the user picks/rotates through is
        // the "use existing project conventions rather than blindly
        // implementing exact fields" equivalent of an avatar system.
        public string ColorKey { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        public DateTime LastActiveUtc { get; set; }
    }
}
