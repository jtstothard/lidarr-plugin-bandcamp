using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    /// <summary>
    /// Models for deserializing Bandcamp search results.
    /// Bandcamp search returns JSON within an HTML page, or can be scraped from the DOM.
    /// These models cover the JSON search API response format.
    /// </summary>
    public class BandcampSearchResponse
    {
        [JsonPropertyName("results")]
        public List<BandcampSearchResult> Results { get; set; } = new();

        [JsonPropertyName("total")]
        public int Total { get; set; }
    }

    public class BandcampSearchResult
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("item_url")]
        public string ItemUrl { get; set; } = "";

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = "";

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; } = "";

        [JsonPropertyName("num_tracks")]
        public int NumTracks { get; set; }

        [JsonPropertyName("art_id")]
        public long ArtId { get; set; }

        /// <summary>
        /// For search results, this contains the artist/band name.
        /// </summary>
        [JsonPropertyName("band_name")]
        public string BandName { get; set; } = "";

        /// <summary>
        /// Album title (when type is "a" for album).
        /// </summary>
        [JsonPropertyName("item_name")]
        public string ItemName { get; set; } = "";
    }
}
