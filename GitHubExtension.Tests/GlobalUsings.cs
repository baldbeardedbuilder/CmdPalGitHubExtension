// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

global using BaldBeardedBuilder.CmdPal.GitHub.Api;
global using BaldBeardedBuilder.CmdPal.GitHub.Auth;
global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using Moq;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
