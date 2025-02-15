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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SonarScanner.MSBuild.Common;
using SonarScanner.MSBuild.PreProcessor.Protobuf;
using SonarScanner.MSBuild.PreProcessor.Test.Infrastructure;
using SonarScanner.MSBuild.PreProcessor.WebServer;
using TestUtilities;

namespace SonarScanner.MSBuild.PreProcessor.Test
{
    [TestClass]
    public class SonarQubeWebServerTest
    {
        private const string ProjectKey = "project-key";
        private const string ProjectBranch = "project-branch";

        private readonly TestDownloader downloader;
        private readonly Version version;
        private readonly TestLogger logger;

        private SonarQubeWebServer sut;

        public SonarQubeWebServerTest()
        {
            downloader = new TestDownloader();
            version = new Version("9.9");
            logger = new TestLogger();
        }

        [TestInitialize]
        public void Init() =>
            sut = new SonarQubeWebServer(downloader, version, logger, null);

        [TestCleanup]
        public void Cleanup() =>
            sut?.Dispose();

        [DataTestMethod]
        [DataRow("7.9.0.5545", DisplayName = "7.9 LTS")]
        [DataRow("8.0.0.18670", DisplayName = "SonarCloud")]
        [DataRow("8.8.0.1121")]
        [DataRow("9.0.0.1121")]
        [DataRow("10.15.0.1121")]
        public void WarnIfDeprecated_ShouldNotWarn(string sqVersion)
        {
            sut = new SonarQubeWebServer(Mock.Of<IDownloader>(), new Version(sqVersion), logger, null);

            logger.Warnings.Should().BeEmpty();
        }

        [DataTestMethod]
        [DataRow("6.7.0.2232")]
        [DataRow("7.0.0.2232")]
        [DataRow("7.8.0.2232")]
        public void WarnIfDeprecated_ShouldWarn(string sqVersion)
        {
            sut = new SonarQubeWebServer(Mock.Of<IDownloader>(), new Version(sqVersion), logger, null);

            logger.AssertSingleWarningExists("The version of SonarQube you are using is deprecated. Analyses will fail starting 6.0 release of the Scanner for .NET");
        }

        [DataTestMethod]
        [DataRow("{ }")]
        [DataRow(@"{ ""isValidLicense"": false }")]
        public async Task IsServerLicenseValid_Commercial_AuthNotForced_LicenseIsInvalid(string responseContent)
        {
            var response = new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(responseContent) };
            var downloaderMock = Mock.Of<IDownloader>(x => x.DownloadResource(It.IsAny<string>()) == Task.FromResult(response) && x.GetBaseUrl() == "host");
            sut = new SonarQubeWebServer(downloaderMock, version, logger, null);

            var isValid = await sut.IsServerLicenseValid();

            isValid.Should().BeFalse();
            logger.AssertSingleErrorExists("Your SonarQube instance seems to have an invalid license. Please check it. Server url: host");
            logger.AssertNoWarningsLogged();
        }

        [TestMethod]
        public async Task IsServerLicenseValid_Commercial_AuthNotForced_LicenseIsValid()
        {
            var response = new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(@"{ ""isValidLicense"": true }") };
            var downloaderMock = Mock.Of<IDownloader>(x => x.DownloadResource(It.IsAny<string>()) == Task.FromResult(response));
            sut = new SonarQubeWebServer(downloaderMock, version, logger, null);

            var isValid = await sut.IsServerLicenseValid();

            isValid.Should().BeTrue();
            logger.AssertNoErrorsLogged();
            logger.AssertNoWarningsLogged();
        }

        [TestMethod]
        public async Task IsServerLicenseValid_Commercial_AuthForced_WithoutCredentials_ShouldReturnFalseAndLogError()
        {
            var downloaderMock = Mock.Of<IDownloader>(x => x.DownloadResource("api/editions/is_valid_license") == Task.FromResult(new HttpResponseMessage { StatusCode = HttpStatusCode.Unauthorized }));
            sut = new SonarQubeWebServer(downloaderMock, version, logger, null);

            var result = await sut.IsServerLicenseValid();

            result.Should().BeFalse();
            logger.AssertSingleErrorExists("Unauthorized: Access is denied due to invalid credentials. Please check the authentication parameters.");
            logger.AssertNoWarningsLogged();
        }

        [TestMethod]
        public async Task IsServerLicenseValid_ServerNotLicensed()
        {
            var response = new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound, Content = new StringContent(@"{""errors"":[{""msg"":""License not found""}]}") };
            var downloaderMock = Mock.Of<IDownloader>(x => x.DownloadResource(It.IsAny<string>()) == Task.FromResult(response) && x.GetBaseUrl() == "host");
            sut = new SonarQubeWebServer(downloaderMock, version, logger, null);

            var result = await sut.IsServerLicenseValid();

            result.Should().BeFalse();
            logger.AssertSingleErrorExists("Your SonarQube instance seems to have an invalid license. Please check it. Server url: host");
            logger.AssertNoWarningsLogged();
        }

        [TestMethod]
        public async Task IsServerLicenseValid_CE_SkipLicenseCheck()
        {
            var response = new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound, Content = new StringContent(@"{""errors"":[{""msg"":""Unknown url: /api/editions/is_valid_license""}]}") };
            var downloaderMock = Mock.Of<IDownloader>(x => x.DownloadResource(It.IsAny<string>()) == Task.FromResult(response));
            sut = new SonarQubeWebServer(downloaderMock, version, logger, null);

            var result = await sut.IsServerLicenseValid();

            result.Should().BeTrue();
            logger.AssertNoErrorsLogged();
            logger.AssertNoWarningsLogged();
        }

        [TestMethod]
        public async Task IsServerLicenseValid_RequestUrl()
        {
            var mockDownloader = new Mock<IDownloader>();
            mockDownloader.Setup(x => x.DownloadResource("api/editions/is_valid_license"))
                          .ReturnsAsync(new HttpResponseMessage { Content = new StringContent(@"{ ""isValidLicense"": true }"), StatusCode = HttpStatusCode.OK}).Verifiable();
            sut = new SonarQubeWebServer(mockDownloader.Object, version, logger, null);

            var isValid = await sut.IsServerLicenseValid();

            isValid.Should().BeTrue();
            mockDownloader.Verify();
        }

        [TestMethod]
        [DataRow("foo bar", "my org")]
        public async Task DownloadQualityProfile_OrganizationProfile_QualityProfileUrlContainsOrganization(string projectKey, string organization)
        {
            const string profileKey = "orgProfile";
            const string language = "cs";
            var downloadResult = Tuple.Create(true, $"{{ profiles: [{{\"key\":\"{profileKey}\",\"name\":\"profile1\",\"language\":\"{language}\"}}]}}");
            var downloaderMock = Mock.Of<IDownloader>(x => x.TryDownloadIfExists(It.IsAny<string>(), It.IsAny<bool>()) == Task.FromResult(downloadResult));
            sut = new SonarQubeWebServer(downloaderMock, new Version("9.9"), logger, organization);

            var result = await sut.DownloadQualityProfile(projectKey, null, language);

            result.Should().Be(profileKey);
        }

        [TestMethod]
        [DataRow("foo bar", "my org")]
        public async Task DownloadQualityProfile_SQ62OrganizationProfile_QualityProfileUrlDoesNotContainsOrganization(string projectKey, string organization)
        {
            const string profileKey = "orgProfile";
            const string language = "cs";
            var qualityProfileUrl = $"api/qualityprofiles/search?project={WebUtility.UrlEncode($"{projectKey}")}";
            var downloadResult = Tuple.Create(true, $"{{ profiles: [{{\"key\":\"{profileKey}\",\"name\":\"profile1\",\"language\":\"{language}\"}}]}}");
            var downloaderMock = Mock.Of<IDownloader>(x => x.TryDownloadIfExists(qualityProfileUrl, It.IsAny<bool>()) == Task.FromResult(downloadResult));
            sut = new SonarQubeWebServer(downloaderMock, new Version("6.2"), logger, organization);

            var result = await sut.DownloadQualityProfile(projectKey, null, language);

            result.Should().Be(profileKey);
        }

        [TestMethod]
        public void DownloadQualityProfile_MultipleQPForSameLanguage_ShouldThrow()
        {
            var downloadResult = Tuple.Create(true, "{ profiles: [{\"key\":\"profile1k\",\"name\":\"profile1\",\"language\":\"cs\", \"isDefault\": false}, {\"key\":\"profile4k\",\"name\":\"profile4\",\"language\":\"cs\", \"isDefault\": true}]}");
            var downloaderMock = Mock.Of<IDownloader>(x => x.TryDownloadIfExists("api/qualityprofiles/search?project=foo+bar", It.IsAny<bool>()) == Task.FromResult(downloadResult));
            sut = new SonarQubeWebServer(downloaderMock, new Version("9.9"), logger, null);

            // ToDo: This behavior is confusing, and not all the parsing errors should lead to this. See: https://github.com/SonarSource/sonar-scanner-msbuild/issues/1468
            ((Func<string>)(() => sut.DownloadQualityProfile("foo bar", null, "cs").Result))
                .Should()
                .ThrowExactly<AggregateException>()
                .WithInnerExceptionExactly<AnalysisException>()
                .WithMessage("It seems that you are using an old version of SonarQube which is not supported anymore. Please update to at least 6.7.");
        }

        [TestMethod]
        public void DownloadProperties_Sq63()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.TryDownloadIfExists("api/settings/values?component=comp", It.IsAny<bool>()))
                          .ReturnsAsync(Tuple.Create(true, @"{ settings: [
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
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("6.3"), logger, null);

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
        public async Task DownloadProperties_Sq63_NoComponentSettings_FallsBackToCommon()
        {
            const string componentName = "nonexistent-component";
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.TryDownloadIfExists($"api/settings/values?component={componentName}", It.IsAny<bool>())).ReturnsAsync(Tuple.Create(false, (string)null));
            downloaderMock.Setup(x => x.Download("api/settings/values", It.IsAny<bool>())).ReturnsAsync(@"{ settings: [ { key: ""key"", value: ""42"" } ]}");
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("6.3"), logger, null);

            var result = await sut.DownloadProperties(componentName, null);

            result.Should().ContainSingle().And.ContainKey("key");
            result["key"].Should().Be("42");
        }

        [TestMethod]
        public async Task DownloadProperties_Sq63_MissingValue_Throws()
        {
            const string componentName = "nonexistent-component";
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.TryDownloadIfExists($"api/settings/values?component={componentName}", It.IsAny<bool>())).ReturnsAsync(Tuple.Create(false, (string)null));
            downloaderMock.Setup(x => x.Download("api/settings/values", It.IsAny<bool>())).ReturnsAsync(@"{ settings: [ { key: ""key"" } ]}");
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("6.3"), logger, null);

            await sut.Invoking(async x => await x.DownloadProperties(componentName, null)).Should().ThrowAsync<ArgumentException>().WithMessage("Invalid property");
        }

        [TestMethod]
        public void DownloadProperties_NullProjectKey_Throws()
        {
            var testSubject = new SonarQubeWebServer(Mock.Of<IDownloader>(), version, logger, null);
            Action act = () => _ = testSubject.DownloadProperties(null, null).Result;

            act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("projectKey");
        }

        [TestMethod]
        public async Task DownloadProperties_ProjectWithBranch_SuccessfullyRetrieveProperties()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.Download("api/properties?resource=foo+bar%3AaBranch", It.IsAny<bool>()))
                          .ReturnsAsync("[{\"key\": \"sonar.property1\",\"value\": \"anotherValue1\"},{\"key\": \"sonar.property2\",\"value\": \"anotherValue2\"}]").Verifiable();
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("5.6"), logger, null);
            var expected = new Dictionary<string, string>
            {
                ["sonar.property1"] = "anotherValue1",
                ["sonar.property2"] = "anotherValue2"
            };

            var result = await sut.DownloadProperties("foo bar", "aBranch");

            result.Should().HaveCount(expected.Count);
            result.Should().Equal(expected);
            downloaderMock.Verify();
        }

        [TestMethod]
        public async Task DownloadProperties_ProjectWithoutBranch_SuccessfullyRetrieveProperties()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.Download("api/properties?resource=foo+bar", It.IsAny<bool>()))
                          .ReturnsAsync("[{\"key\": \"sonar.property1\",\"value\": \"anotherValue1\"},{\"key\": \"sonar.property2\",\"value\": \"anotherValue2\"}]").Verifiable();
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("5.6"), logger, null);
            var expected = new Dictionary<string, string>
            {
                ["sonar.property1"] = "anotherValue1",
                ["sonar.property2"] = "anotherValue2"
            };

            var result = await sut.DownloadProperties("foo bar", null);

            result.Should().HaveCount(expected.Count);
            result.Should().Equal(expected);
            downloaderMock.Verify();
        }

        [TestMethod]
        public async Task DownloadProperties_Old_Forbidden()
        {
            var downloaderMock = Mock.Of<IDownloader>(x => x.Download($"api/properties?resource={ProjectKey}", It.IsAny<bool>()) == Task.FromException<string>(new HttpRequestException("Forbidden")));
            var service = new SonarQubeWebServer(downloaderMock, new Version("1.2.3.4"), logger, null);

            Func<Task> action = async () => await service.DownloadProperties(ProjectKey, null);

            await action.Should().ThrowAsync<HttpRequestException>();
        }

        [TestMethod]
        public void DownloadProperties_Sq63plus_Forbidden()
        {
            var downloaderMock = Mock.Of<IDownloader>(x => x.TryDownloadIfExists(It.IsAny<string>(), It.IsAny<bool>()) == Task.FromException<Tuple<bool, string>>(new HttpRequestException("Forbidden")));
            var service = new SonarQubeWebServer(downloaderMock, new Version("6.3.0.0"), logger, null);

            Action action = () => _ = service.DownloadProperties(ProjectKey, null).Result;

            action.Should().Throw<HttpRequestException>();
        }

        [TestMethod]
        public async Task DownloadProperties_SQ63AndHigherWithProject_ShouldBeEmpty()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.TryDownloadIfExists(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(Tuple.Create(true, "{ settings: [ ] }")).Verifiable();
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("6.3"), logger, null);

            var properties = await sut.DownloadProperties("key", null);

            properties.Should().BeEmpty();
            downloaderMock.Verify();
        }

        [TestMethod]
        public async Task DownloadProperties_OlderThanSQ63_ShouldBeEmpty()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.Download(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync("[]").Verifiable();
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("6.2.9"), logger, null);

            var properties = await sut.DownloadProperties("key", null);

            properties.Should().BeEmpty();
            downloaderMock.Verify();
        }

        [TestMethod]
        public async Task DownloadProperties_SQ63AndHigherWithoutProject_ShouldBeEmpty()
        {
            var downloaderMock = new Mock<IDownloader>();
            downloaderMock.Setup(x => x.TryDownloadIfExists(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(Tuple.Create(false, (string)null)).Verifiable();
            downloaderMock.Setup(x => x.Download(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync("{ settings: [ ] }").Verifiable();
            sut = new SonarQubeWebServer(downloaderMock.Object, new Version("6.3"), logger, null);

            var properties = await sut.DownloadProperties("key", null);

            properties.Should().BeEmpty();
            downloaderMock.Verify();
        }

        [TestMethod]
        public async Task DownloadCache_NullArgument()
        {
            (await sut.Invoking(x => x.DownloadCache(null)).Should().ThrowAsync<ArgumentNullException>()).And.ParamName.Should().Be("localSettings");
        }

        [TestMethod]
        [DataRow("9.8", "", "", "Incremental PR analysis is available starting with SonarQube 9.9 or later.")]
        [DataRow("9.9", "", "", "Incremental PR analysis: ProjectKey parameter was not provided.")]
        [DataRow("9.9", "BestProject", "", "Incremental PR analysis: Base branch parameter was not provided.")]
        public async Task DownloadCache_InvalidArguments(string version, string projectKey, string branch, string debugMessage)
        {
            sut = new SonarQubeWebServer(Mock.Of<IDownloader>(), new Version(version), logger, null);
            var localSettings = CreateLocalSettings(projectKey, branch);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleInfoMessageExists(debugMessage);
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
            using Stream stream = new MemoryStream();
            sut = new SonarQubeWebServer(MockIDownloader(stream), version, logger, null);
            var localSettings = CreateLocalSettings(ProjectKey, null);

            await sut.DownloadCache(localSettings);

            logger.AssertInfoMessageExists($"Incremental PR analysis: Automatically detected base branch 'branch-42' from CI Provider '{provider}'.");
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
            using var environment = new EnvironmentVariableScope().SetVariable(variableName, "wrong_branch");
            using Stream stream = new MemoryStream();
            sut = new SonarQubeWebServer(MockIDownloader(stream), version, logger, null);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);

            await sut.DownloadCache(localSettings);

            logger.AssertSingleInfoMessageExists("Downloading cache. Project key: project-key, branch: project-branch.");
        }

        [TestMethod]
        public async Task DownloadCache_RequestUrl()
        {
            using Stream stream = new MemoryStream();
            var mockDownloader = new Mock<IDownloader>();
            mockDownloader.Setup(x => x.DownloadStream("api/analysis_cache/get?project=project-key&branch=project-branch")).ReturnsAsync(stream).Verifiable();
            sut = new SonarQubeWebServer(mockDownloader.Object, version, logger, null);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            mockDownloader.Verify();
        }

        [TestMethod]
        public async Task DownloadCache_DeserializesMessage()
        {
            using var stream = CreateCacheStream(new SensorCacheEntry { Key = "key", Data = ByteString.CopyFromUtf8("value") });
            sut = new SonarQubeWebServer(MockIDownloader(stream), version, logger, null);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);

            var result = await sut.DownloadCache(localSettings);

            result.Should().ContainSingle();
            result.Single(x => x.Key == "key").Data.ToStringUtf8().Should().Be("value");
            logger.AssertInfoLogged("Downloading cache. Project key: project-key, branch: project-branch.");
        }

        [TestMethod]
        public async Task DownloadCache_WhenDownloadStreamReturnsNull_ReturnsEmpty()
        {
            sut = new SonarQubeWebServer(MockIDownloader(null), version, logger, null);

            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);
            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertNoWarningsLogged();
            logger.AssertNoErrorsLogged(); // There are no errors or warnings logs but we will display an info message in the caller: "Cache data is empty. A full analysis will be performed."
        }

        [TestMethod]
        public async Task DownloadCache_WhenDownloadStreamReturnsEmpty_ReturnsEmpty()
        {
            sut = new SonarQubeWebServer(MockIDownloader(new MemoryStream()), version, logger, null);

            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);
            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.DebugMessages.Should().BeEmpty();
        }

        [TestMethod]
        public async Task DownloadCache_WhenDownloadStreamThrows_ReturnsEmptyAndLogsException()
        {
            var downloaderMock = Mock.Of<IDownloader>(x => x.DownloadStream(It.IsAny<string>()) == Task.FromException<Stream>(new HttpRequestException()));
            sut = new SonarQubeWebServer(downloaderMock, version, logger, null);

            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);
            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleWarningExists("Incremental PR analysis: an error occurred while retrieving the cache entries! Exception of type 'System.Net.Http.HttpRequestException' was thrown.");
        }

        [TestMethod]
        public async Task DownloadCache_WhenCacheStreamReadThrows_ReturnsEmptyAndLogsException()
        {
            var streamMock = new Mock<Stream>();
            streamMock.Setup(x => x.Length).Throws<InvalidOperationException>();
            sut = new SonarQubeWebServer(MockIDownloader(streamMock.Object), version, logger, null);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleWarningExists("Incremental PR analysis: an error occurred while retrieving the cache entries! Operation is not valid due to the current state of the object.");
        }

        [TestMethod]
        public async Task DownloadCache_WhenCacheStreamDeserializeThrows_ReturnsEmptyAndLogsException()
        {
            var invalidProtoStream = new MemoryStream(new byte[] { 42, 42 }); // this is a random byte array that fails deserialization
            sut = new SonarQubeWebServer(MockIDownloader(invalidProtoStream), version, logger, null);
            var localSettings = CreateLocalSettings(ProjectKey, ProjectBranch);

            var result = await sut.DownloadCache(localSettings);

            result.Should().BeEmpty();
            logger.AssertSingleWarningExists("Incremental PR analysis: an error occurred while retrieving the cache entries! While parsing a protocol message, the input ended unexpectedly in the middle of a field.  This could mean either that the input has been truncated or that an embedded message misreported its own length.");
        }

        [TestMethod]
        public async Task DownloadRules_SonarQubeVersion98()
        {
            var testDownloader = new TestDownloader();
            testDownloader.Pages["api/rules/search?f=repo,name,severity,lang,internalKey,templateKey,params,actives&ps=500&qprofile=qp&p=1"] = @" {
            paging: {
                total: 3,
                pageIndex: 1,
                pageSize: 500
            },
            rules: [
                {
                    ""key"": ""csharpsquid:S2757"",
                    ""repo"": ""csharpsquid"",
                    ""type"": ""BUG""
                }
            ]}";
            sut = new SonarQubeWebServer(testDownloader, new Version("9.8"), logger, null);

            var rules = await sut.DownloadRules("qp");

            rules.Should().ContainSingle();

            rules[0].RepoKey.Should().Be("csharpsquid");
            rules[0].RuleKey.Should().Be("S2757");
            rules[0].InternalKeyOrKey.Should().Be("S2757");
            rules[0].Parameters.Should().BeNull();
            rules[0].IsActive.Should().BeFalse();
        }

        [TestMethod]
        public async Task DownloadRules_SonarQubeVersion89()
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
            sut = new SonarQubeWebServer(testDownloader, new Version("8.9"), logger, null);

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
            var stream = new MemoryStream();
            message.WriteDelimitedTo(stream);
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }

        private static IDownloader MockIDownloader(Stream stream) =>
            Mock.Of<IDownloader>(x => x.DownloadStream(It.IsAny<string>()) == Task.FromResult(stream));

        private static ProcessedArgs CreateLocalSettings(string projectKey, string branch, string organization = "placeholder", string token = "placeholder")
        {
            var args = new Mock<ProcessedArgs>();
            args.SetupGet(a => a.ProjectKey).Returns(projectKey);
            args.SetupGet(a => a.Organization).Returns(organization);
            args.Setup(a => a.TryGetSetting(It.Is<string>(x => x == SonarProperties.PullRequestBase), out branch)).Returns(!string.IsNullOrWhiteSpace(branch));
            args.Setup(a => a.TryGetSetting(It.Is<string>(x => x == SonarProperties.SonarUserName), out token)).Returns(!string.IsNullOrWhiteSpace(token));
            return args.Object;
        }
    }
}
