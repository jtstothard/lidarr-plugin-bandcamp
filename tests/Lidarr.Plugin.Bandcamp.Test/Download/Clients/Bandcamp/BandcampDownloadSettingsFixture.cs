using System.Linq;
using NzbDrone.Core.Download.Clients.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsFixture
    {
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
            Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Session cookies are required"));
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
        public void DefaultDownloadPath_IsEmpty()
        {
            var settings = new BandcampDownloadSettings();
            Assert.Equal("", settings.DownloadPath);
        }
    }
}
