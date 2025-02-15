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
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SonarScanner.MSBuild.Common;
using TestUtilities;

namespace SonarScanner.MSBuild.Shim.Test
{
    [TestClass]
    public class SonarScannerWrapperTests
    {
        private const string ExpectedConsoleMessagePrefix = "Args passed to dummy scanner: ";

        public TestContext TestContext { get; set; }

        #region Tests

        [TestMethod]
        public void Execute_WhenConfigIsNull_Throws()
        {
            // Arrange
            var testSubject = new SonarScannerWrapper(new TestLogger());
            Action act = () => testSubject.Execute(null, new string[] { }, string.Empty);

            // Act & Assert
            act.Should().ThrowExactly<ArgumentNullException>().And.ParamName.Should().Be("config");
        }

        [TestMethod]
        public void Execute_WhenUserCmdLineArgumentsIsNull_Throws()
        {
            // Arrange
            var testSubject = new SonarScannerWrapper(new TestLogger());
            Action act = () => testSubject.Execute(new AnalysisConfig(), null, string.Empty);

            // Act & Assert
            act.Should().ThrowExactly<ArgumentNullException>().And.ParamName.Should().Be("userCmdLineArguments");
        }

        [TestMethod]
        public void Execute_WhenFullPropertiesFilePathIsNull_ReturnsFalse()
        {
            // Arrange
            var testSubject = new SonarScannerWrapper(new TestLogger());
            var result = testSubject.Execute(new AnalysisConfig(), new List<string>(), null);

            // Act & Assert
            result.Should().BeFalse();
        }

        [TestMethod]
        public void Ctor_WhenLoggerIsNull_Throws()
        {
            // Arrange
            Action act = () => new SonarScannerWrapper(null);

            // Act & Assert
            act.Should().ThrowExactly<ArgumentNullException>().And.ParamName.Should().Be("logger");
        }

        [TestMethod]
        public void SonarScannerHome_NoMessageIfNotAlreadySet()
        {
            // Arrange
            var testLogger = new TestLogger();

            using var scope = new EnvironmentVariableScope().SetVariable(SonarScannerWrapper.SonarScannerHomeVariableName, null);
            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = "C:\\working\\dir" };
            var mockRunner = new MockProcessRunner(executeResult: true);

            // Act
            var success = ExecuteJavaRunnerIgnoringAsserts(config, Enumerable.Empty<string>(), testLogger, "c:\\file.exe", "d:\\properties.prop", mockRunner);

            // Assert
            VerifyProcessRunOutcome(mockRunner, testLogger, "C:\\working\\dir", success, true);
            testLogger.AssertMessageNotLogged(Resources.MSG_SonarScannerHomeIsSet);
        }

        [TestMethod]
        public void SonarScannerHome_MessageLoggedIfAlreadySet()
        {
            using var scope = new EnvironmentVariableScope().SetVariable(SonarScannerWrapper.SonarScannerHomeVariableName, "some_path");
            var testLogger = new TestLogger();
            var mockRunner = new MockProcessRunner(executeResult: true);
            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = "c:\\workingDir" };

            var success = ExecuteJavaRunnerIgnoringAsserts(config, Enumerable.Empty<string>(), testLogger, "c:\\exePath", "f:\\props.txt", mockRunner);

            VerifyProcessRunOutcome(mockRunner, testLogger, "c:\\workingDir", success, true);
            testLogger.AssertInfoMessageExists(Resources.MSG_SonarScannerHomeIsSet);
        }

        [TestMethod]
        public void SonarScanner_StandardAdditionalArgumentsPassed()
        {
            // Arrange
            var logger = new TestLogger();
            var mockRunner = new MockProcessRunner(executeResult: true);
            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = "c:\\work" };

            // Act
            var success = ExecuteJavaRunnerIgnoringAsserts(config, Enumerable.Empty<string>(), logger, "c:\\exe.Path", "d:\\propertiesFile.Path", mockRunner);

            // Assert
            VerifyProcessRunOutcome(mockRunner, logger, "c:\\work", success, true);
        }

        [TestMethod]
        public void SonarScanner_CmdLineArgsArePassedThroughToTheWrapperAndAppearFirst()
        {
            var logger = new TestLogger();
            var userArgs = new[] { "-Dsonar.login=me", "-Dsonar.password=my.pwd", "-Dsonar.token=token" };
            var mockRunner = new MockProcessRunner(executeResult: true);

            var success = ExecuteJavaRunnerIgnoringAsserts(
                new AnalysisConfig { SonarScannerWorkingDirectory = "D:\\dummyWorkingDirectory" },
                userArgs,
                logger,
                "c:\\dummy.exe",
                "c:\\foo.properties",
                mockRunner);

            VerifyProcessRunOutcome(mockRunner, logger, "D:\\dummyWorkingDirectory", success, true);
            CheckStandardArgsPassed(mockRunner, "c:\\foo.properties");
            var loginIndex = CheckArgExists("-Dsonar.login=me", mockRunner);
            var passwordIndex = CheckArgExists("-Dsonar.password=my.pwd", mockRunner);
            var tokenIndex = CheckArgExists("-Dsonar.token=token", mockRunner);
            var propertiesFileIndex = CheckArgExists(SonarScannerWrapper.ProjectSettingsFileArgName, mockRunner);
            propertiesFileIndex.Should().BeGreaterThan(loginIndex, "User arguments should appear first");
            propertiesFileIndex.Should().BeGreaterThan(passwordIndex, "User arguments should appear first");
            propertiesFileIndex.Should().BeGreaterThan(tokenIndex, "User arguments should appear first");
        }

        [TestMethod]
        public void SonarScanner_SensitiveArgsPassedOnCommandLine()
        {
            // Check that sensitive arguments from the config are passed on the command line

            // Arrange
            var logger = new TestLogger();
            var mockRunner = new MockProcessRunner(executeResult: true);
            var userArgs = new[] { "-Dxxx=yyy", "-Dsonar.password=cmdline.password" };

            // Create a config file containing sensitive arguments
            var fileSettings = new AnalysisProperties
            {
                new(SonarProperties.ClientCertPassword, "client certificate password"),
                new(SonarProperties.SonarPassword, "file.password - should not be returned"),
                new(SonarProperties.SonarUserName, "file.username - should not be returned"),
                new(SonarProperties.SonarToken, "token - should not be returned"),
                new("file.not.sensitive.key", "not sensitive value")
            };

            var testDir = TestUtils.CreateTestSpecificFolderWithSubPaths(TestContext);
            var settingsFilePath = Path.Combine(testDir, "fileSettings.txt");
            fileSettings.Save(settingsFilePath);

            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = testDir };
            config.SetSettingsFilePath(settingsFilePath);

            // Act
            var success = ExecuteJavaRunnerIgnoringAsserts(config, userArgs, logger, "c:\\foo.exe", "c:\\foo.props", mockRunner);

            // Assert
            VerifyProcessRunOutcome(mockRunner, logger, testDir, success, true);

            CheckStandardArgsPassed(mockRunner, "c:\\foo.props");

            // Non-sensitive values from the file should not be passed on the command line
            CheckArgDoesNotExist("file.not.sensitive.key", mockRunner);
            CheckArgDoesNotExist(SonarProperties.SonarUserName, mockRunner);
            CheckArgDoesNotExist(SonarProperties.SonarPassword, mockRunner);
            CheckArgDoesNotExist(SonarProperties.ClientCertPassword, mockRunner);
            CheckArgDoesNotExist(SonarProperties.SonarToken, mockRunner);

            var clientCertPwdIndex = CheckArgExists("-Dsonar.clientcert.password=client certificate password", mockRunner); // sensitive value from file
            var userPwdIndex = CheckArgExists("-Dsonar.password=cmdline.password", mockRunner); // sensitive value from cmd line: overrides file value

            var propertiesFileIndex = CheckArgExists(SonarScannerWrapper.ProjectSettingsFileArgName, mockRunner);

            propertiesFileIndex.Should().BeGreaterThan(clientCertPwdIndex, "User arguments should appear first");
            propertiesFileIndex.Should().BeGreaterThan(userPwdIndex, "User arguments should appear first");
        }

        [TestMethod]
        public void SonarScanner_NoUserSpecifiedEnvVars_SONARSCANNEROPTSIsNotPassed()
        {
            // Arrange
            var logger = new TestLogger();
            var mockRunner = new MockProcessRunner(executeResult: true);
            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = "c:\\work" };

            // Act
            var success = ExecuteJavaRunnerIgnoringAsserts(config, Enumerable.Empty<string>(), logger, "c:\\exe.Path", "d:\\propertiesFile.Path", mockRunner);

            // Assert
            VerifyProcessRunOutcome(mockRunner, logger, "c:\\work", success, true);

            mockRunner.SuppliedArguments.EnvironmentVariables.Count.Should().Be(0);

            // #656: Check that the JVM size is not set by default
            // https://github.com/SonarSource/sonar-scanner-msbuild/issues/656
            logger.InfoMessages.Should().NotContain(msg => msg.Contains("SONAR_SCANNER_OPTS"));
        }

        [TestMethod]
        public void SonarScanner_UserSpecifiedEnvVars_OnlySONARSCANNEROPTSIsPassed()
        {
            // Arrange
            var logger = new TestLogger();
            var mockRunner = new MockProcessRunner(executeResult: true);
            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = "c:\\work" };

            using (new EnvironmentVariableScope())
            {
                // the SONAR_SCANNER_OPTS variable should be passed through explicitly,
                // but not other variables
                Environment.SetEnvironmentVariable("Foo", "xxx");
                Environment.SetEnvironmentVariable("SONAR_SCANNER_OPTS", "-Xmx2048m");
                Environment.SetEnvironmentVariable("Bar", "yyy");

                // Act
                var success = ExecuteJavaRunnerIgnoringAsserts(config, Enumerable.Empty<string>(), logger, "c:\\exe.Path", "d:\\propertiesFile.Path", mockRunner);

                // Assert
                VerifyProcessRunOutcome(mockRunner, logger, "c:\\work", success, true);
            }

            CheckEnvVarExists("SONAR_SCANNER_OPTS", "-Xmx2048m", mockRunner);
            mockRunner.SuppliedArguments.EnvironmentVariables.Count.Should().Be(1);
            logger.InfoMessages.Should().Contain(msg => msg.Contains("SONAR_SCANNER_OPTS"));
            logger.InfoMessages.Should().Contain(msg => msg.Contains("-Xmx2048m"));
        }

        [TestMethod]
        public void WrapperError_Success_NoStdErr() =>
            TestWrapperErrorHandling(executeResult: true, addMessageToStdErr: false, expectedOutcome: true);

        [TestMethod]
        [WorkItem(202)] // SONARMSBRU-202
        public void WrapperError_Success_StdErr() =>
            TestWrapperErrorHandling(executeResult: true, addMessageToStdErr: true, expectedOutcome: true);

        [TestMethod]
        public void WrapperError_Fail_NoStdErr() =>
            TestWrapperErrorHandling(executeResult: false, addMessageToStdErr: false, expectedOutcome: false);

        [TestMethod]
        public void WrapperError_Fail_StdErr() =>
            TestWrapperErrorHandling(executeResult: false, addMessageToStdErr: true, expectedOutcome: false);

        [TestMethod]
        public void FindScannerExe_ReturnsScannerCliBat()
        {
            // Act
            var scannerCliScriptPath = SonarScannerWrapper.FindScannerExe();

            // Assert
            scannerCliScriptPath.Should().EndWithEquivalentOf(@"\bin\sonar-scanner.bat");
        }

#endregion Tests

#region Private methods

        private static bool ExecuteJavaRunnerIgnoringAsserts(AnalysisConfig config, IEnumerable<string> userCmdLineArguments, ILogger logger, string exeFileName, string propertiesFileName, IProcessRunner runner)
        {
            using (new AssertIgnoreScope())
            {
                return SonarScannerWrapper.ExecuteJavaRunner(config, userCmdLineArguments, logger, exeFileName, propertiesFileName, runner);
            }
        }

        private void TestWrapperErrorHandling(bool executeResult, bool addMessageToStdErr, bool expectedOutcome)
        {
            // Arrange
            var logger = new TestLogger();
            var mockRunner = new MockProcessRunner(executeResult);

            var config = new AnalysisConfig() { SonarScannerWorkingDirectory = "C:\\working" };

            if (addMessageToStdErr)
            {
                logger.LogError("Dummy error");
            }

            // Act
            var success = ExecuteJavaRunnerIgnoringAsserts(config, Enumerable.Empty<string>(), logger, "c:\\bar.exe", "c:\\props.xml", mockRunner);

            // Assert
            VerifyProcessRunOutcome(mockRunner, logger, "C:\\working", success, expectedOutcome);
        }

#endregion Private methods

        private static void VerifyProcessRunOutcome(MockProcessRunner mockRunner, TestLogger testLogger, string expectedWorkingDir, bool actualOutcome, bool expectedOutcome)
        {
            actualOutcome.Should().Be(expectedOutcome);

            mockRunner.SuppliedArguments.WorkingDirectory.Should().Be(expectedWorkingDir);

            if (actualOutcome)
            {
                // Errors can still be logged when the process completes successfully, so
                // we don't check the error log in this case
                testLogger.AssertInfoMessageExists(Resources.MSG_SonarScannerCompleted);
            }
            else
            {
                testLogger.AssertErrorsLogged();
                testLogger.AssertErrorLogged(Resources.ERR_SonarScannerExecutionFailed);
            }
        }

        /// <summary>
        /// Checks that the argument exists, and returns the start position of the argument in the list of
        /// concatenated arguments so we can check that the arguments are passed in the correct order
        /// </summary>
        private static int CheckArgExists(string expectedArg, MockProcessRunner mockRunner)
        {
            var allArgs = string.Join(" ", mockRunner.SuppliedArguments.CmdLineArgs);
            var index = allArgs.IndexOf(expectedArg, StringComparison.Ordinal);
            index.Should().BeGreaterThan(-1, "Expected argument was not found. Arg: '{0}', all args: '{1}'", expectedArg, allArgs);
            return index;
        }

        private static void CheckStandardArgsPassed(MockProcessRunner mockRunner, string expectedPropertiesFilePath) =>
            CheckArgExists("-Dproject.settings=" + expectedPropertiesFilePath, mockRunner); // should always be passing the properties file

        private static void CheckArgDoesNotExist(string argToCheck, MockProcessRunner mockRunner)
        {
            var allArgs = mockRunner.SuppliedArguments.GetEscapedArguments();
            var index = allArgs.IndexOf(argToCheck, StringComparison.Ordinal);
            index.Should().Be(-1, "Not expecting to find the argument. Arg: '{0}', all args: '{1}'", argToCheck, allArgs);
        }

        private static void CheckEnvVarExists(string varName, string expectedValue, MockProcessRunner mockRunner)
        {
            mockRunner.SuppliedArguments.EnvironmentVariables.ContainsKey(varName).Should().BeTrue();
            mockRunner.SuppliedArguments.EnvironmentVariables[varName].Should().Be(expectedValue);
        }
    }
}
