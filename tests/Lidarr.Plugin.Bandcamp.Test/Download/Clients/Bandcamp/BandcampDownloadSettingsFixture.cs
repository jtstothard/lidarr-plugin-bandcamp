using System.Linq;
using NzbDrone.Core.Download.Clients.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsFixture
    {
        [Theory]
        [InlineData("fan@example.com", "", "Password")]
        [InlineData("", "password", "Email")]
        public void PartialCredentialsAreRejectedEvenWithCookie(string email, string password, string field)
        {
            var settings = new BandcampDownloadSettings
            {
                Email = email, Password = password, Cookies = "identity=usable", DownloadPath = "/downloads"
            };
            Assert.Contains(settings.Validate().Errors, e => e.PropertyName == field);
        }

        [Fact]
        public void Validate_AllFieldsSet_ReturnsNoErrors()
        {
            var settings = new BandcampDownloadSettings
            {
                Cookies = "identity=testcookie; session=abc",
                DownloadPath = "/tmp/bandcamp-downloads"
            };

            var result = settings.Validate();

            Assert.True(result.IsValid, $"Expected no validation errors but got: {string.Join(", ", result.Errors.Select(e => e.ErrorMessage))}");
        }

        [Fact]
        public void Validate_EmptyCookies_ReturnsError()
        {
            var settings = new BandcampDownloadSettings
            {
                Cookies = "",
                DownloadPath = "/tmp/bandcamp-downloads"
            };

            var result = settings.Validate();

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
            Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("email and password, or a session cookie"));
        }

        [Fact]
        public void Validate_EmailAndPasswordWithoutCookie_ReturnsNoErrors()
        {
            var settings = new BandcampDownloadSettings
            {
                Email = "fan@example.com",
                Password = "hunter2",
                DownloadPath = "/tmp/bandcamp-downloads"
            };

            var result = settings.Validate();

            Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => e.ErrorMessage)));
        }

        [Fact]
        public void Validate_EmailWithoutPasswordOrCookie_ReturnsPasswordError()
        {
            var settings = new BandcampDownloadSettings
            {
                Email = "fan@example.com",
                DownloadPath = "/tmp/bandcamp-downloads"
            };

            var result = settings.Validate();

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Password");
        }

        [Fact]
        public void Validate_PasswordWithoutEmailOrCookie_ReturnsEmailError()
        {
            var settings = new BandcampDownloadSettings
            {
                Password = "hunter2",
                DownloadPath = "/tmp/bandcamp-downloads"
            };

            var result = settings.Validate();

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Email");
        }

        [Fact]
        public void Validate_EmptyDownloadPath_ReturnsError()
        {
            var settings = new BandcampDownloadSettings
            {
                Cookies = "identity=testcookie",
                DownloadPath = ""
            };

            var result = settings.Validate();

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "DownloadPath");
            Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Download path is required"));
        }

        [Fact]
        public void Validate_DefaultSettings_ReturnsErrors()
        {
            var settings = new BandcampDownloadSettings();

            var result = settings.Validate();

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
            Assert.Contains(result.Errors, e => e.PropertyName == "DownloadPath");
        }

        [Fact]
        public void DefaultCookies_IsEmpty()
        {
            var settings = new BandcampDownloadSettings();
            Assert.Equal("", settings.Cookies);
        }

        [Fact]
        public void DefaultCredentials_AreEmpty()
        {
            var settings = new BandcampDownloadSettings();
            Assert.Equal("", settings.Email);
            Assert.Equal("", settings.Password);
            Assert.Equal("", settings.VerificationCode);
        }

        [Fact]
        public void Validate_VerificationCodeIsOptional()
        {
            var settings = new BandcampDownloadSettings
            {
                Email = "fan@example.com",
                Password = "hunter2",
                VerificationCode = "123456",
                DownloadPath = "/downloads"
            };

            Assert.True(settings.Validate().IsValid);
        }

        [Fact]
        public void DefaultDownloadPath_IsEmpty()
        {
            var settings = new BandcampDownloadSettings();
            Assert.Equal("", settings.DownloadPath);
        }
    }
}
