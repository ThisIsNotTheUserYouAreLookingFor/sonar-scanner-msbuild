﻿/*
 * SonarScanner for MSBuild
 * Copyright (C) 2016-2023 SonarSource SA
 * mailto:info AT sonarsource DOT com
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

namespace CSProj1
{
    /* FxCop violations:
    *
    * CA1801 (unused params) -- not in quality profile
    * CA1822 (Bar can be static) -- not in quality profile
    * CA2201 (Do not raise reserved exceptions)
    * CA1303 (Do not pass literals as localized params)

    * SonarLint violations
    *   S1134 (no 'F i x m e' comments)
    *   S1135 (no 'T O D O' comments)  -- not in quality profile
    *   S107 (method should not have to many parameters) -- not in quality profile, and only triggered with the right parameters
    *
    * Also triggered:
    *   common-cs:InsufficientCommentDensity -- not in quality profile
    *
    */

    public class Foo
    {
        public string Bar
        {
            get
            {
                Console.WriteLine("Hello world");

                // violates S1135
                //TODO: lorem ipsum

                // violates S1134
                return String.Empty; //FIXME please
            }
            set
            {
                // Violates FxCop's CA2201: Do not raise reserved exception types (major issue)
                throw new Exception("Hello world");
            }
        }

        public int test(int i1, int i2, int i3)
        {
        	return i1 + i2 + i3;
        }
    }
}
