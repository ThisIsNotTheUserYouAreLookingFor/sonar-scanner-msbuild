﻿/*
 * SonarScanner for .NET
 * Copyright (C) 2016-2023 SonarSource SA
 * mailto: info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using SonarScanner.MSBuild.Common;
using SonarScanner.MSBuild.PreProcessor.Protobuf;
using SonarScanner.MSBuild.PreProcessor.Test.Infrastructure;
using SonarScanner.MSBuild.PreProcessor.WebServer;
using TestUtilities;

namespace SonarScanner.MSBuild.PreProcessor.Test
{
    [TestClass]
    public class SonarCloudWebServerTest
    {
        private const string ProjectKey = "project-key";
        private const string ProjectBranch = "project-branch";
        private const string Token = "42";
        private const string Organization = "org42";

        private readonly TestDownloader downloader;
        private readonly Version version;
        private readonly TestLogger logger;

        private SonarCloudWebServer sut;

        public SonarCloudWebServerTest()
        {
            downloader = new TestDownloader();
            version = new Version("5.6");
            logger = new TestLogger();
        }

        [TestInitialize]
        public void Init() => sut = new SonarCloudWebServer(downloader, version, logger, Organization);

        [TestCleanup]
        public void Cleanup() =>
            sut?.Dispose();

        [TestMethod]
        public void Ctor_OrganizationNull_ShouldThrow() =>
            ((Func<SonarCloudWebServer>)(() => new SonarCloudWebServer(downloader, version, logger, null))).Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("organization");

        [TestMethod]
        public async Task IsLicenseValid_IsSonarCloud_ShouldReturnTrue()
        {
            sut = new SonarCloudWebServer(downloader, version, logger, Organization);

            (await sut.IsServerLicenseValid()).Should().BeTrue();
        }

        [TestMethod]
        public async Task IsLicenseValid_AlwaysValid()
        {
            downloader.Pages["api/editions/is_valid_license"] = @"{ ""isValidLicense"": false }";

            (await sut.IsServerLicenseValid()).Should().BeTrue();
        }

        [TestMethod]
        public void WarnIfDeprecated_ShouldNotWarn()
        {
            sut = new SonarCloudWebServer(downloader, new Version("0.0.1"), logger, Organization);

            logger.Warnings.Should().BeEmpty();
        }

        [TestMethod]
        public void DownloadProperties_Success()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.TryDownloadIfExists(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(Tuple.Create(true, @"{ settings: [
                  {
                    key: ""sonar.core.id"",
                    value: ""AVrrKaIfChAsLlov22f0"",
                    inherited: true
                  },
                  {
                    key: ""sonar.exclusions"",
                    values: [
                      ""myfile"",
                      ""myfile2""
                    ]
                  },
                  {
                    key: ""sonar.junit.reportsPath"",
                    value: ""testing.xml""
                  },
                  {
                    key: ""sonar.issue.ignore.multicriteria"",
                    fieldValues: [
                        {
                            resourceKey: ""prop1"",
                            ruleKey: """"
                        },
                        {
                            resourceKey: ""prop2"",
                            ruleKey: """"
                        }
                    ]
                  }
                ]}"));
            sut = new SonarCloudWebServer(downloaderMock.Object, version, logger, Organization);

            var result = sut.DownloadProperties("comp", null).Result;

            result.Should().HaveCount(7);
            result["sonar.exclusions"].Should().Be("myfile,myfile2");
            result["sonar.junit.reportsPath"].Should().Be("testing.xml");
            result["sonar.issue.ignore.multicriteria.1.resourceKey"].Should().Be("prop1");
            result["sonar.issue.ignore.multicriteria.1.ruleKey"].Should().Be(string.Empty);
            result["sonar.issue.ignore.multicriteria.2.resourceKey"].Should().Be("prop2");
            result["sonar.issue.ignore.multicriteria.2.ruleKey"].Should().Be(string.Empty);
        }

        [TestMethod]
        public void DownloadProperties_NullProjectKey_Throws()
        {
            sut = new SonarCloudWebServer(downloader, version, logger, Organization);

            Action act = () => _ = sut.DownloadProperties(null, null).Result;

            act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("projectKey");
        }

        [TestMethod]
        public async Task IsServerLicenseValid_AlwaysTrue()
        {
            var isValid = await sut.IsServerLicenseValid();

            isValid.Should().BeTrue();
            logger.AssertDebugMessageExists("SonarCloud detected, skipping license check.");
        }

        [TestMethod]
        public async Task DownloadCache_NullArgument()
        {
            (await sut.Invoking(x => x.DownloadCache(null)).Should().ThrowAsync<ArgumentNullException>()).And.ParamName.Should().Be("localSettings");
        }

        [TestMethod]
        [DataRow("", "", "", "Incremental PR analysis: ProjectKey parameter was not provided.")]
        [DataRow("project", "", "", "Incremental PR analysis: Base branch parameter was not provided.")]
        [DataRow("project", "branch", "", "Incremental PR analysis: Token parameter was not provided.")]
        [DataRow("project", "branch", "token", "Incremental PR analysis: CacheBaseUrl was not successfully retrieved.")]
        public async Task DownloadCache_InvalidArguments(string projectKey, string branch, string token, string infoMessage)
        {
            sut = new SonarCloudWebServer(MockIDownloader(), version, logger, Organization);
            var localSettings = CreateLocalSettings(projectKey, branch, Organization, token);

            var res = await sut.DownloadCache(localSettings);

            res.Should().BeEmpty();
            logger.AssertSingleInfoMessageExists(infoMessage);
        }

        [DataTestMethod]
        [DataRow("Jenkins", "ghprbTargetBranch")]
        [DataRow("Jenkins", "gitlabTargetBranch")]
        [DataRow("Jenkins", "BITBUCKET_TARGET_BRANCH")]
        [DataRow("GitHub Actions", "GITHUB_BASE_REF")]
        [DataRow("GitLab", "CI_MERGE_REQUEST_TARGET_BRANCH_NAME")]
        [DataRow("BitBucket Pipelines", "BITBUCKET_PR_DESTINATION_BRANCH")]
        public async Task DownloadCache_AutomaticallyDeduceBaseBranch(string provider, string variableName)
        {
            using var environment = new EnvironmentVariableScope().SetVariable(variableName, "branch-42");
            const string organization = "org42";
            using var stream = new MemoryStream();
            var handler = MockHttpHandler("http://myhost:222/v1/sensor_cache/prepare_read?organization=org42&project=project-key&branch=branch-42", "https://www.ephemeralUrl.com", stream);
            sut = new SonarCloudWebServer(MockIDownloader("http://myhost:222"), version, logger, organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, null, organization, Token);

            await sut.DownloadCache(localSettings);

            logger.AssertInfoMessageExists($"Incremental PR analysis: Automatically detected base branch 'branch-42' from CI Provider '{provider}'.");
            handler.VerifyAll();
        }

        [DataTestMethod]
        [DataRow("ghprbTargetBranch")]
        [DataRow("gitlabTargetBranch")]
        [DataRow("BITBUCKET_TARGET_BRANCH")]
        [DataRow("GITHUB_BASE_REF")]
        [DataRow("CI_MERGE_REQUEST_TARGET_BRANCH_NAME")]
        [DataRow("BITBUCKET_PR_DESTINATION_BRANCH")]
        public async Task DownloadCache_UserInputSupersedesAutomaticDetection(string variableName)
        {
            using var environment = new EnvironmentVariableScope().SetVariable(variableName, "wrong-branch");
            const string organization = "org42";
            using var stream = new MemoryStream();
            var handler = MockHttpHandler("http://myhost:222/v1/sensor_cache/prepare_read?organization=org42&project=project-key&branch=project-branch", "https://www.ephemeralUrl.com", stream);
            sut = new SonarCloudWebServer(MockIDownloader("http://myhost:222"), version, logger, organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, organization, Token);

            await sut.DownloadCache(localSettings);

            logger.AssertSingleInfoMessageExists("Downloading cache. Project key: project-key, branch: project-branch.");
            handler.VerifyAll();
        }

        [TestMethod]
        [DataRow("http://cacheBaseUrl:222", "http://cachebaseurl:222/v1/sensor_cache/prepare_read?organization=org42&project=project-key&branch=project-branch")]
        [DataRow("http://cacheBaseUrl:222/", "http://cachebaseurl:222/v1/sensor_cache/prepare_read?organization=org42&project=project-key&branch=project-branch")]
        [DataRow("http://cacheBaseUrl:222/sonar/", "http://cachebaseurl:222/sonar/v1/sensor_cache/prepare_read?organization=org42&project=project-key&branch=project-branch")]
        public async Task DownloadCache_RequestUrl(string cacheBaseUrl, string cacheFullUrl)
        {
            const string organization = "org42";
            using var stream = new MemoryStream();
            var handler = MockHttpHandler(cacheFullUrl, "https://www.ephemeralUrl.com", stream);

            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, organization, Token);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleDebugMessageExists($"Incremental PR Analysis: Requesting 'prepare_read' from {cacheFullUrl}");
            handler.VerifyAll();
        }

        [DataTestMethod]
        [DataRow(SonarProperties.SonarUserName)]
        [DataRow(SonarProperties.SonarToken)]
        public async Task DownloadCache_CacheHit(string tokenKey)
        {
            const string cacheBaseUrl = "https://www.cacheBaseUrl.com";
            var cacheFullUrl = $"https://www.cacheBaseUrl.com/v1/sensor_cache/prepare_read?organization={Organization}&project=project-key&branch=project-branch";
            using var stream = CreateCacheStream(new SensorCacheEntry { Key = "key", Data = ByteString.CopyFromUtf8("value") });
            var handler = MockHttpHandler(cacheFullUrl, "https://www.ephemeralUrl.com", stream);
            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, Organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, Organization, Token, tokenKey);

            var result = await sut.DownloadCache(localSettings);

            result.Should().ContainSingle();
            result.Single(x => x.Key == "key").Data.ToStringUtf8().Should().Be("value");
            logger.AssertInfoLogged("Downloading cache. Project key: project-key, branch: project-branch.");
            handler.VerifyAll();
        }

        [TestMethod]
        public async Task DownloadCache_PrepareRead_UnsuccessfulResponse()
        {
            const string cacheBaseUrl = "https://www.cacheBaseUrl.com";
            var cacheFullUrl = $"https://www.cacheBaseUrl.com/v1/sensor_cache/prepare_read?organization={Organization}&project=project-key&branch=project-branch";
            var handler = MockHttpHandler(cacheFullUrl, "irrelevant", HttpStatusCode.Forbidden);
            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, Organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, Organization, Token);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleDebugMessageExists("Incremental PR analysis: an error occurred while retrieving the cache entries! 'prepare_read' did not respond successfully.");
            handler.VerifyAll();
        }

        [TestMethod]
        public async Task DownloadCache_PrepareRead_EmptyResponse()
        {
            const string cacheBaseUrl = "https://www.cacheBaseUrl.com";
            var cacheFullUrl = $"https://www.cacheBaseUrl.com/v1/sensor_cache/prepare_read?organization={Organization}&project=project-key&branch=project-branch";
            var handler = MockHttpHandler(cacheFullUrl, string.Empty);
            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, Organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, Organization, Token);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleDebugMessageExists("Incremental PR analysis: an error occurred while retrieving the cache entries! 'prepare_read' response was empty.");
            handler.VerifyAll();
        }

        [TestMethod]
        public async Task DownloadCache_PrepareRead_CacheDisabled()
        {
            const string cacheBaseUrl = "https://www.cacheBaseUrl.com";
            var cacheFullUrl = $"https://www.cacheBaseUrl.com/v1/sensor_cache/prepare_read?organization={Organization}&project=project-key&branch=project-branch";
            var handler = MockHttpHandler(cacheFullUrl, $@"{{ ""enabled"": ""false"", ""url"":""https://www.sonarsource.com"" }}");
            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, Organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, Organization, Token);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleDebugMessageExists("Incremental PR analysis: an error occurred while retrieving the cache entries! 'prepare_read' response: { Enabled = False, Url = https://www.sonarsource.com }");
            handler.VerifyAll();
        }

        [TestMethod]
        public async Task DownloadCache_PrepareRead_CacheEnabledButUrlMissing()
        {
            const string cacheBaseUrl = "https://www.cacheBaseUrl.com";
            var cacheFullUrl = $"https://www.cacheBaseUrl.com/v1/sensor_cache/prepare_read?organization={Organization}&project=project-key&branch=project-branch";
            var handler = MockHttpHandler(cacheFullUrl, $@"{{ ""enabled"": ""true"" }}");
            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, Organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, Organization, Token);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleDebugMessageExists("Incremental PR analysis: an error occurred while retrieving the cache entries! 'prepare_read' response: { Enabled = True, Url =  }");
            handler.VerifyAll();
        }

        [TestMethod]
        public async Task DownloadCache_ThrowException()
        {
            const string cacheBaseUrl = "https://www.cacheBaseUrl.com";
            var cacheFullUrl = $"https://www.cacheBaseUrl.com/v1/sensor_cache/prepare_read?organization={Organization}&project=project-key&branch=project-branch";

            using var stream = new MemoryStream(new byte[] { 42, 42 }); // this is a random byte array that fails deserialization
            var handler = MockHttpHandler(cacheFullUrl, "https://www.ephemeralUrl.com", stream);
            sut = new SonarCloudWebServer(MockIDownloader(cacheBaseUrl), version, logger, Organization, handler.Object);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch, Organization, Token);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleWarningExists("Incremental PR analysis: an error occurred while retrieving the cache entries! Found invalid data while decoding.");
            logger.AssertNoErrorsLogged();
            handler.VerifyAll();
        }

        [TestMethod]
        public async Task DownloadRules_SonarCloud()
        {
            var testDownloader = new TestDownloader();
            testDownloader.Pages["api/rules/search?f=repo,name,severity,lang,internalKey,templateKey,params,actives&ps=500&qprofile=qp&p=1"] = @" {
            total: 3,
            p: 1,
            ps: 500,
            rules: [
                {
                    ""key"": ""csharpsquid:S2757"",
                    ""repo"": ""csharpsquid"",
                    ""type"": ""BUG""
                }
            ]}";
            sut = new SonarCloudWebServer(testDownloader, version, logger, Organization);

            var rules = await sut.DownloadRules("qp");

            rules.Should().ContainSingle();

            rules[0].RepoKey.Should().Be("csharpsquid");
            rules[0].RuleKey.Should().Be("S2757");
            rules[0].InternalKeyOrKey.Should().Be("S2757");
            rules[0].Parameters.Should().BeNull();
            rules[0].IsActive.Should().BeFalse();
        }

        private static Stream CreateCacheStream(IMessage message)
        {
            using var stream = new MemoryStream();
            message.WriteDelimitedTo(stream);
            stream.Seek(0, SeekOrigin.Begin);

            var compressed = new MemoryStream();
            using var compressor = new GZipStream(compressed, CompressionMode.Compress, true);
            stream.CopyTo(compressor);

            compressor.Close();
            compressed.Seek(0, SeekOrigin.Begin);
            return compressed;
        }

        private static IDownloader MockIDownloader(string cacheBaseUrl = null)
        {
            var serverSettingsJson = cacheBaseUrl is not null
                                         ? $"{{\"settings\":[{{ \"key\":\"sonar.sensor.cache.baseUrl\",\"value\": \"{cacheBaseUrl}\" }}]}}"
                                         : "{\"settings\":[]}";

            var mock = new Mock<IDownloader>();
            mock.Setup(x => x.Download(It.IsAny<string>(), It.IsAny<bool>())).Returns(Task.FromResult(serverSettingsJson));
            mock.Setup(x => x.TryDownloadIfExists(It.IsAny<string>(), It.IsAny<bool>())).Returns(Task.FromResult(new Tuple<bool, string>(false, string.Empty)));
            return mock.Object;
        }

        private static Mock<HttpMessageHandler> MockHttpHandler(string cacheFullUrl, string prepareReadResponse, HttpStatusCode prepareReadResponseCode = HttpStatusCode.OK)
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(x => x.RequestUri == new Uri(cacheFullUrl) && x.Headers.Any(h => h.Key == "Authorization" && h.Value.Contains($"Bearer {Token}"))),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = prepareReadResponseCode,
                    Content = new StringContent(prepareReadResponse),
                }))
                .Verifiable();

            return handler;
        }

        private static Mock<HttpMessageHandler> MockHttpHandler(string cacheFullUrl, string ephemeralCacheUrl, Stream cacheData)
        {
            var mock = MockHttpHandler(cacheFullUrl, $"{{ \"enabled\": \"true\", \"url\":\"{ephemeralCacheUrl}\" }}");

            mock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(x => x.RequestUri == new Uri(ephemeralCacheUrl)),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StreamContent(cacheData),
                }))
                .Verifiable();

            return mock;
        }

        private static ProcessedArgs CreateLocalSettings(string projectKey,
                                                         string branch,
                                                         string organization = "placeholder",
                                                         string token = "placeholder",
                                                         string tokenKey = SonarProperties.SonarToken)
        {
            var args = new Mock<ProcessedArgs>();
            args.SetupGet(a => a.ProjectKey).Returns(projectKey);
            args.SetupGet(a => a.Organization).Returns(organization);
            args.Setup(a => a.TryGetSetting(It.Is<string>(x => x == SonarProperties.PullRequestBase), out branch)).Returns(!string.IsNullOrWhiteSpace(branch));
            args.Setup(a => a.TryGetSetting(It.Is<string>(x => x == tokenKey), out token)).Returns(!string.IsNullOrWhiteSpace(token));
            return args.Object;
        }
    }
}
