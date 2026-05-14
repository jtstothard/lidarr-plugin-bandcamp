using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    internal class BandcampRetagContext
    {
        public string ArtistName { get; set; } = string.Empty;
        public string? ArtistMusicBrainzId { get; set; }
        public string AlbumTitle { get; set; } = string.Empty;
        public string? AlbumMusicBrainzId { get; set; }
        public string? AlbumType { get; set; }
        public string? AlbumDisambiguation { get; set; }
        public DateTime? AlbumReleaseDate { get; set; }
        public string[] Genres { get; set; } = Array.Empty<string>();
        public BandcampRetagReleaseContext? PreferredRelease { get; set; }
        public List<BandcampRetagTrackContext> Tracks { get; set; } = new();
    }

    internal class BandcampRetagReleaseContext
    {
        public string? ReleaseMusicBrainzId { get; set; }
        public string? ReleaseArtistMusicBrainzId { get; set; }
        public string? ReleaseStatus { get; set; }
        public string? Label { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int DiscCount { get; set; }
        public Dictionary<int, string> MediaByDisc { get; set; } = new();
    }

    internal class BandcampRetagTrackContext
    {
        public int AbsoluteTrackNumber { get; set; }
        public int MediumNumber { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? RecordingMusicBrainzId { get; set; }
        public string? ReleaseTrackMusicBrainzId { get; set; }
    }
}
