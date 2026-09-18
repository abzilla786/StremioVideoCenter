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

        // Which configured addon this stream came from. NOT part of the
        // addon's JSON response (no [DataMember]) - set by
        // StremioAddonRegistry.GetAllStreamsAsync after deserialization, so
        // that a list aggregated across multiple addons can still be traced
        // back to its source. Null for a stream constructed any other way.
        public string SourceAddonUrl { get; set; }
    }
}