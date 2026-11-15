using System.Linq;
using NzbDrone.Core.Download.Clients.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsFixture
    {
        [Fact]
        public void LegacyJsonKeepsIndependentAuthAndDowngradeReadableSecrets()
        {
            const string legacy = "{\"Cookies\":\"identity=legacy\",\"Email\":\"legacy@example.test\",\"Password\":\"legacy-password\",\"VerificationCode\":\"one-time\",\"DownloadPath\":\"/legacy\"}";
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<BandcampDownloadSettings>(legacy)!;
            Assert.Equal("", settings.AuthConfiguration);
            Assert.True(settings.Validate().IsValid);
            var roundTrip = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(settings));
            Assert.Equal("identity=legacy", (string?)roundTrip["Cookies"]);
            Assert.Equal("legacy@example.test", (string?)roundTrip["Email"]);
            Assert.Equal("legacy-password", (string?)roundTrip["Password"]);
            Assert.Equal("one-time", (string?)roundTrip["VerificationCode"]);
            Assert.Equal("/legacy", (string?)roundTrip["DownloadPath"]);
            var indexer = Newtonsoft.Json.JsonConvert.DeserializeObject<NzbDrone.Core.Indexers.Bandcamp.BandcampIndexerSettings>(
                "{\"Cookies\":\"identity=separate-account\",\"EarlyReleaseLimit\":9,\"BaseUrl\":\"https://bandcamp.com\"}")!;
            Assert.Equal("", indexer.AuthConfiguration);
            Assert.Equal("identity=separate-account", indexer.Cookies);
            Assert.Equal(9, indexer.EarlyReleaseLimit);
            Assert.True(indexer.Validate().IsValid);
        }

        [Theory]
        [InlineData("legacy@example.test", "", "Password")]
        [InlineData("", "legacy-password", "Email")]
        public void SharedSelectionDoesNotEraseIncompleteLegacyCredentials(string email, string password, string field)
        {
            var settings = new BandcampDownloadSettings
            {
                AuthConfiguration = "Explicit owner", Cookies = "identity=legacy", Email = email, Password = password, DownloadPath = "/downloads"
            };
            Assert.Contains(settings.Validate().Errors, e => e.PropertyName == field);
            Assert.Equal("identity=legacy", settings.Cookies);
        }

        [Fact]
        public void NamedConfigurationRequiresNoDuplicateSecretsAndKeepsLegacyFields()
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<BandcampDownloadSettings>(
                "{\"AuthConfiguration\":\"My fan account\",\"DownloadPath\":\"/downloads\"}")!;
            Assert.True(settings.Validate().IsValid);
            var indexer = System.Text.Json.JsonSerializer.Deserialize<NzbDrone.Core.Indexers.Bandcamp.BandcampIndexerSettings>(
                "{\"AuthConfiguration\":\"My fan account\"}")!;
            Assert.True(indexer.Validate().IsValid);
        }

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
