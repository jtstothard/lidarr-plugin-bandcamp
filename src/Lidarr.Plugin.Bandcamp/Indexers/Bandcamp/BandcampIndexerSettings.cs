using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    public class BandcampIndexerSettingsValidator : AbstractValidator<BandcampIndexerSettings>
    {
        public BandcampIndexerSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Cookies).NotEmpty()
                .When(c => !string.IsNullOrWhiteSpace(c.BaseUrl) && string.IsNullOrWhiteSpace(c.AuthConfiguration))
                .WithMessage("Session cookies are required to access Bandcamp");
        }
    }

    public class BandcampIndexerSettings : IIndexerSettings
    {
        private static readonly BandcampIndexerSettingsValidator Validator = new BandcampIndexerSettingsValidator();

        public BandcampIndexerSettings()
        {
            BaseUrl = "https://bandcamp.com";
        }

        [FieldDefinition(0, Label = "Session Cookies", Type = FieldType.Textbox, HelpText = "Paste the 'identity' cookie value from your browser's Bandcamp cookies. In browser DevTools: Application → Cookies → bandcamp.com → copy the 'identity' value.", Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; } = "";

        [FieldDefinition(1, Label = "Base URL", Type = FieldType.Url, HelpText = "Bandcamp base URL", Advanced = true)]
        public string BaseUrl { get; set; }

        [FieldDefinition(2, Type = FieldType.Number, Label = "Early Download Limit", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Unit = "days", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(3, Label = "Shared Authentication", Type = FieldType.Textbox, HelpText = "Exact name of a saved Bandcamp download client that owns authentication (its Shared Authentication must be empty). Uses its current session for search. Leave empty to keep the legacy independent cookie. Existing local cookie is retained for downgrade, not updated by shared renewal.")]
        public string AuthConfiguration { get; set; } = "";

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
