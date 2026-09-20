using System.Collections.Generic;
using System.Runtime.Serialization;

namespace StremioVideoCenter.Models
{
    [DataContract]
    public class StremioStream
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        // Official Stremio protocol field, intended as the eventual
        // replacement for "title" (the SDK docs explicitly say title "will
        // soon be deprecated in favor of stream.description"). Captured
        // additively, kept fully separate from Title - never merged, never
        // used as a fallback for it. As addons migrate to this field,
        // Title may go empty for them while this is populated instead;
        // both are preserved so nothing is silently lost either way.
        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "url")]
        public string Url { get; set; }

        [DataMember(Name = "ytId")]
        public string YtId { get; set; }

        [DataMember(Name = "infoHash")]
        public string InfoHash { get; set; }

        [DataMember(Name = "fileIdx")]
        public int? FileIdx { get; set; }

        [DataMember(Name = "externalUrl")]
        public string ExternalUrl { get; set; }

        // Standard Stremio protocol field, previously not captured at all -
        // DataContractJsonSerializer silently ignores unknown JSON
        // properties, so any addon that sent this was having it discarded
        // before any of our code ever ran. Purely additive: existing
        // fields/behaviour are completely unchanged, and streams from
        // addons that don't send this simply leave it null.
        [DataMember(Name = "behaviorHints")]
        public StremioStreamBehaviorHints BehaviorHints { get; set; }

        // Standard Stremio protocol field, same story as BehaviorHints -
        // previously discarded silently, now captured if an addon sends it.
        [DataMember(Name = "subtitles")]
        public List<StremioSubtitle> Subtitles { get; set; }

        // Which configured addon this stream came from. NOT part of the
        // addon's JSON response (no [DataMember]) - set by
        // StremioAddonRegistry.GetAllStreamsAsync after deserialization, so
        // that a list aggregated across multiple addons can still be traced
        // back to its source. Null for a stream constructed any other way.
        public string SourceAddonUrl { get; set; }
    }

    // =============================================================
    // BEHAVIOR HINTS
    // =============================================================
    //
    // Stremio addons optionally attach this. VideoSize is a real, safe,
    // already-structured number - no parsing/guessing needed to surface it
    // as "file size". Filename is the addon's own literal specification of
    // the release/file name, likewise safe to carry through as-is.

    [DataContract]
    public class StremioStreamBehaviorHints
    {
        [DataMember(Name = "filename")]
        public string Filename { get; set; }

        [DataMember(Name = "videoSize")]
        public long? VideoSize { get; set; }

        [DataMember(Name = "bingeGroup")]
        public string BingeGroup { get; set; }

        [DataMember(Name = "notWebReady")]
        public bool? NotWebReady { get; set; }
    }

    // =============================================================
    // SUBTITLE
    // =============================================================

    [DataContract]
    public class StremioSubtitle
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "url")]
        public string Url { get; set; }

        [DataMember(Name = "lang")]
        public string Lang { get; set; }
    }
}