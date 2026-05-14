using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsValidator : AbstractValidator<BandcampDownloadSettings>
    {
        public BandcampDownloadSettingsValidator()
        {
            RuleFor(c => c.Cookies).NotEmpty()
                .WithMessage("Session cookies are required to download purchased albums from Bandcamp");

            RuleFor(c => c.DownloadPath).NotEmpty()
                .WithMessage("Download path is required");

            RuleFor(c => c.DownloadPath).IsValidPath()
                .When(c => !c.DownloadPath.IsNullOrWhiteSpace());
        }
    }

    public class BandcampDownloadSettings : IProviderConfig
    {
        private static readonly BandcampDownloadSettingsValidator Validator = new BandcampDownloadSettingsValidator();

        public BandcampDownloadSettings()
        {
            DownloadPath = "";
            Cookies = "";
            MediaFormat = "FLAC";
        }

        [FieldDefinition(0, Label = "Session Cookies", Type = FieldType.Textbox, HelpText = "Paste the 'identity' cookie value from your browser's Bandcamp cookies. In browser DevTools: Application → Cookies → bandcamp.com → copy the 'identity' value.", Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; }

        [FieldDefinition(1, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where Bandcamp downloads will be saved before import")]
        public string DownloadPath { get; set; }

        [FieldDefinition(2, Label = "Media Format", Type = FieldType.Select, SelectOptions = typeof(BandcampMediaFormat), HelpText = "Audio format to request from Bandcamp")]
        public string MediaFormat { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public enum BandcampMediaFormat
    {
        [FieldOption(Label = "FLAC")]
        flac,
        [FieldOption(Label = "ALAC")]
        alac,
        [FieldOption(Label = "WAV")]
        wav,
        [FieldOption(Label = "AIFF")]
        aiff,
        [FieldOption(Label = "V0 MP3")]
        mp3_v0,
        [FieldOption(Label = "320 MP3")]
        mp3_320,
        [FieldOption(Label = "OGG Vorbis")]
        ogg_vorbis,
        [FieldOption(Label = "AAC")]
        aac
    }
}
