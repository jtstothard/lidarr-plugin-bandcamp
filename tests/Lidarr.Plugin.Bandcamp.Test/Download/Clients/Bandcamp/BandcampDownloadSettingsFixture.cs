using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Download.Clients.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsFixture
    {
        [Fact]
        public void Validate_AllFieldsSet_ReturnsNoErrors()
        {
            // Arrange
            var settings = new BandcampDownloadSettings
            {
                Cookies = "identity=testcookie; session=abc",
                DownloadPath = "/tmp/bandcamp-downloads",
                MediaFormat = "FLAC"
            };

            // Act
            var result = settings.Validate();

            // Assert
            Assert.True(result.IsValid, $"Expected no validation errors but got: {string.Join(", ", result.Errors.Select(e => e.ErrorMessage))}");
        }

        [Fact]
        public void Validate_EmptyCookies_ReturnsError()
        {
            // Arrange
            var settings = new BandcampDownloadSettings
            {
                Cookies = "",
                DownloadPath = "/tmp/bandcamp-downloads",
                MediaFormat = "FLAC"
            };

            // Act
            var result = settings.Validate();

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
            Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Session cookies are required"));
        }

        [Fact]
        public void Validate_NullCookies_ReturnsError()
        {
            // Arrange
            var settings = new BandcampDownloadSettings
            {
                Cookies = null!,
                DownloadPath = "/tmp/bandcamp-downloads",
                MediaFormat = "FLAC"
            };

            // Act
            var result = settings.Validate();

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
        }

        [Fact]
        public void Validate_EmptyDownloadPath_ReturnsError()
        {
            // Arrange
            var settings = new BandcampDownloadSettings
            {
                Cookies = "identity=testcookie",
                DownloadPath = "",
                MediaFormat = "FLAC"
            };

            // Act
            var result = settings.Validate();

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "DownloadPath");
            Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Download path is required"));
        }

        [Fact]
        public void Validate_BothMissing_ReturnsMultipleErrors()
        {
            // Arrange
            var settings = new BandcampDownloadSettings
            {
                Cookies = "",
                DownloadPath = "",
                MediaFormat = "FLAC"
            };

            // Act
            var result = settings.Validate();

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal(2, result.Errors.Count);
            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
            Assert.Contains(result.Errors, e => e.PropertyName == "DownloadPath");
        }

        [Fact]
        public void Validate_DefaultSettings_ReturnsErrors()
        {
            // Arrange — default constructor sets empty strings
            var settings = new BandcampDownloadSettings();

            // Act
            var result = settings.Validate();

            // Assert — defaults should fail validation since Cookies and DownloadPath are empty
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
            Assert.Contains(result.Errors, e => e.PropertyName == "DownloadPath");
        }

        [Fact]
        public void DefaultMediaFormat_IsFLAC()
        {
            // Arrange & Act
            var settings = new BandcampDownloadSettings();

            // Assert
            Assert.Equal("FLAC", settings.MediaFormat);
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

        [Fact]
        public void MediaFormatEnum_ContainsExpectedValues()
        {
            // Verify the enum has all expected format options
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.flac));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.alac));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.wav));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.aiff));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.mp3_v0));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.mp3_320));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.ogg_vorbis));
            Assert.True(System.Enum.IsDefined(typeof(BandcampMediaFormat), BandcampMediaFormat.aac));
        }

        [Fact]
        public void Validate_AllMediaFormats_AcceptedWhenOtherFieldsValid()
        {
            // Verify that all media format values pass validation when cookies/path are set
            var formats = new[] { "FLAC", "ALAC", "WAV", "AIFF", "mp3_v0", "mp3_320", "ogg_vorbis", "aac" };

            foreach (var format in formats)
            {
                var settings = new BandcampDownloadSettings
                {
                    Cookies = "identity=test",
                    DownloadPath = "/tmp/downloads",
                    MediaFormat = format
                };

                var result = settings.Validate();
                Assert.True(result.IsValid, $"Format '{format}' should be valid but got: {string.Join(", ", result.Errors.Select(e => e.ErrorMessage))}");
            }
        }
    }
}
