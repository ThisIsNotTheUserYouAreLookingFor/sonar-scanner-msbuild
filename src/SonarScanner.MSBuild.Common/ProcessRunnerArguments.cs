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
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SonarScanner.MSBuild.Common
{
    /// <summary>
    /// Data class containing parameters required to execute a new process
    /// </summary>
    public class ProcessRunnerArguments
    {
        public ProcessRunnerArguments(string exeName, bool isBatchScript)
        {
            if (string.IsNullOrWhiteSpace(exeName))
            {
                throw new ArgumentNullException(nameof(exeName));
            }

            ExeName = exeName;
            IsBatchScript = isBatchScript;

            TimeoutInMilliseconds = Timeout.Infinite;
        }

        #region Public properties

        public string ExeName { get; }

        /// <summary>
        /// Non-sensitive command line arguments (i.e. ones that can safely be logged). Optional.
        /// </summary>
        public IEnumerable<string> CmdLineArgs { get; set; }

        public string WorkingDirectory { get; set; }

        public int TimeoutInMilliseconds { get; set; }

        private bool IsBatchScript { get; set; }

        /// <summary>
        /// Additional environments variables that should be set/overridden for the process. Can be null.
        /// </summary>
        public IDictionary<string, string> EnvironmentVariables { get; set; }

        public string GetEscapedArguments()
        {
            if (CmdLineArgs == null)
            {
                return null;
            }

            return string.Join(" ", CmdLineArgs.Select(a => IsBatchScript ? EscapeShellArgument(a) : EscapeArgument(a)));
        }

        private string EscapeShellArgument(string argument)
        {
            argument = argument?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(argument))
            {
                return argument;
            }

            return NeedsToBeEnclosedInDoubleQuotes(argument)
                ? EncloseInDoubleQuotes(argument)
                : EscapeSpecialCharacter(argument);

            static string EscapeSpecialCharacter(string argument) =>
                argument.Aggregate(new StringBuilder(argument.Length),
                    static (sb, c) => c is '^' or '>' or '<' or '&' or '|'
                        ? sb.Append($"^^^{c}")
                        : sb.Append(c),
                    static sb => sb.ToString());

            static bool NeedsToBeEnclosedInDoubleQuotes(string argument)
                => argument.Any(c => c is ' ' or '\t' or ',' or ';' or '\u00FF' or '=' or '"');

            static string EncloseInDoubleQuotes(string argument)
            {
                if (IsEnclosedInDoubleQuotes(argument))
                {
                    // Remove any existing outer double quotes.
                    argument = argument.Substring(1, argument.Length - 2);
                }
                argument = argument.Replace(@"""", @""""""); // Any inline double quote need to escaped by doubling " -> ""
                argument = $@"""{argument}"""; // Enclose in double quotes
                // each backslash before a double quote must be escaped by four backslash:
                // \" -> \\\\"
                // \\" -> \\\\\\\\"
                argument = Regex.Replace(argument, @"(\\*)""", @"$1$1$1$1""");
                return argument;
            }

            static bool IsEnclosedInDoubleQuotes(string argument) =>
                argument is { Length: >= 2 } && argument[0] == '"' && argument[argument.Length - 1] == '"';
        }

        /// <summary>
        /// Returns the string that should be used when logging command line arguments
        /// (sensitive data will have been removed)
        /// </summary>
        public string AsLogText()
        {
            if (CmdLineArgs == null)
            { return null; }

            var hasSensitiveData = false;

            var sb = new StringBuilder();

            foreach (var arg in CmdLineArgs)
            {
                if (ContainsSensitiveData(arg))
                {
                    hasSensitiveData = true;
                }
                else
                {
                    sb.Append(arg);
                    sb.Append(" ");
                }
            }

            if (hasSensitiveData)
            {
                sb.Append(Resources.MSG_CmdLine_SensitiveCmdLineArgsAlternativeText);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Determines whether the text contains sensitive data that
        /// should not be logged/written to file
        /// </summary>
        public static bool ContainsSensitiveData(string text)
        {
            Debug.Assert(SonarProperties.SensitivePropertyKeys != null, "SensitiveDataMarkers array should not be null");

            if (text == null)
            {
                return false;
            }

            return SonarProperties.SensitivePropertyKeys.Any(marker => text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) > -1);
        }

        /// <summary>
        /// The CreateProcess Win32 API call only takes 1 string for all arguments.
        /// Ultimately, it is the responsibility of each program to decide how to split this string into multiple arguments.
        ///
        /// See:
        /// https://blogs.msdn.microsoft.com/oldnewthing/20100917-00/?p=12833/
        /// https://blogs.msdn.microsoft.com/twistylittlepassagesallalike/2011/04/23/everyone-quotes-command-line-arguments-the-wrong-way/
        /// http://www.daviddeley.com/autohotkey/parameters/parameters.htm
        /// </summary>
        private static string EscapeArgument(string arg)
        {
            Debug.Assert(arg != null, "Not expecting an argument to be null");

            var sb = new StringBuilder();

            sb.Append("\"");
            for (var i = 0; i < arg.Length; i++)
            {
                var numberOfBackslashes = 0;
                for (; i < arg.Length && arg[i] == '\\'; i++)
                {
                    numberOfBackslashes++;
                }

                if (i == arg.Length)
                {
                    //
                    // Escape all backslashes, but let the terminating
                    // double quotation mark we add below be interpreted
                    // as a meta-character.
                    //
                    sb.Append('\\', numberOfBackslashes * 2);
                }
                else if (arg[i] == '"')
                {
                    //
                    // Escape all backslashes and the following
                    // double quotation mark.
                    //
                    sb.Append('\\', numberOfBackslashes * 2 + 1);
                    sb.Append(arg[i]);
                }
                else
                {
                    //
                    // Backslashes aren't special here.
                    //
                    sb.Append('\\', numberOfBackslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append("\"");

            return sb.ToString();
        }

        #endregion Public properties
    }
}
