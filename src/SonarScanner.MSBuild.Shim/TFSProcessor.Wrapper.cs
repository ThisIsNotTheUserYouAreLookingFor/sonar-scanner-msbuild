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
using System.Diagnostics;
using System.IO;
using SonarScanner.MSBuild.Common;
using SonarScanner.MSBuild.Shim.Interfaces;

namespace SonarScanner.MSBuild.Shim
{
    public class TfsProcessorWrapper : ITfsProcessor
    {
        private readonly ILogger logger;

        public TfsProcessorWrapper(ILogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool Execute(AnalysisConfig config, IEnumerable<string> userCmdLineArguments, String fullPropertiesFilePath)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (userCmdLineArguments == null)
            {
                throw new ArgumentNullException(nameof(userCmdLineArguments));
            }

            return InternalExecute(config, logger, userCmdLineArguments, fullPropertiesFilePath);
        }

        #region Private methods

        private static bool InternalExecute(AnalysisConfig config, ILogger logger, IEnumerable<string> userCmdLineArguments, String fullPropertiesFilePath)
        {
            var exeFileName = FindProcessorExe();
            return ExecuteProcessorRunner(config, logger, exeFileName, userCmdLineArguments, fullPropertiesFilePath, new ProcessRunner(logger));
        }

        private static string FindProcessorExe()
        {
            var execFolder = Path.GetDirectoryName(typeof(TfsProcessorWrapper).Assembly.Location);
            return Path.Combine(execFolder, "SonarScanner.MSBuild.TFSProcessor.exe");
        }

        public /* for test purposes */ static bool ExecuteProcessorRunner(AnalysisConfig config, ILogger logger, string exeFileName, IEnumerable<string> userCmdLineArguments, string propertiesFileName, IProcessRunner runner)
        {
            Debug.Assert(File.Exists(exeFileName), "The specified exe file does not exist: " + exeFileName);
            Debug.Assert(File.Exists(propertiesFileName), "The specified properties file does not exist: " + propertiesFileName);

            logger.LogInfo(Resources.MSG_TFSProcessorCalling);

            Debug.Assert(!string.IsNullOrWhiteSpace(config.SonarScannerWorkingDirectory), "The working dir should have been set in the analysis config");
            Debug.Assert(Directory.Exists(config.SonarScannerWorkingDirectory), "The working dir should exist");

            var converterArgs = new ProcessRunnerArguments(exeFileName, !PlatformHelper.IsWindows())
            {
                CmdLineArgs = userCmdLineArguments,
                WorkingDirectory = config.SonarScannerWorkingDirectory,
            };

            var success = runner.Execute(converterArgs);

            if (success)
            {
                logger.LogInfo(Resources.MSG_TFSProcessorCompleted);
            }
            else
            {
                logger.LogError(Resources.ERR_TFSProcessorExecutionFailed);
            }
            return success;
        }

        #endregion Private methods
    }
}
