using System;
using System.Security.Cryptography;
using System.Text;
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
            // Either email and password (credential login) or a session cookie is required.
            RuleFor(c => c.Cookies).NotEmpty()
                .When(c => c.Email.IsNullOrWhiteSpace() && c.Password.IsNullOrWhiteSpace())
                .WithMessage("Enter your Bandcamp email and password, or a session cookie");

            RuleFor(c => c.Password).NotEmpty()
                .When(c => !c.Email.IsNullOrWhiteSpace() && c.Cookies.IsNullOrWhiteSpace())
                .WithMessage("Password is required when an email is set");

            RuleFor(c => c.Email).NotEmpty()
                .When(c => !c.Password.IsNullOrWhiteSpace() && c.Cookies.IsNullOrWhiteSpace())
                .WithMessage("Email is required when a password is set");

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
            Email = "";
            Password = "";
            VerificationCode = "";
        }

        [FieldDefinition(0, Label = "Email", Type = FieldType.Textbox, HelpText = "Bandcamp account email. With a password set, the plugin logs in itself and keeps the session cookie below up to date.", Privacy = PrivacyLevel.Password)]
        public string Email { get; set; }

        [FieldDefinition(1, Label = "Password", Type = FieldType.Password, HelpText = "Bandcamp account password.", Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        // Used once on the next login, then cleared automatically.
        [FieldDefinition(2, Label = "Verification Code", Type = FieldType.Textbox, HelpText = "Only when Bandcamp emails you a code for a new device: enter it here and save. It is used once, then cleared.", Privacy = PrivacyLevel.Password)]
        public string VerificationCode { get; set; }

        // Keeps its name so saved configs and downgrades still work. Filled in automatically after a login.
        [FieldDefinition(3, Label = "Session Cookie (optional)", Type = FieldType.Textbox, HelpText = "Optional fallback: the 'identity' cookie value from your browser (DevTools: Application → Cookies → bandcamp.com → 'identity'). Written automatically after a successful login.", Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; }

        [FieldDefinition(4, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where Bandcamp downloads will be saved before import")]
        public string DownloadPath { get; set; }

        /// <summary>Whether both an email and a password are set (credential login is possible).</summary>
        public bool HasCredentials()
        {
            return !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
        }

        /// <summary>A hash of the email and password, so they aren't kept around in plain text.</summary>
        internal string CredentialKey()
        {
            var bytes = Encoding.UTF8.GetBytes(Email + "\0" + Password);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
