---
title: Install the extension
description: Build and deploy GitHub for Command Palette on Windows, then open it from PowerToys.
---

You can bring your GitHub inbox and repositories into Command Palette. For now, you'll need to build the extension from source.

:::note[Release availability]
There aren't published releases yet. Check [GitHub Releases](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/releases) for updates. These instructions don't require a Store listing or a WinGet package.
:::

## What you'll need

- Windows with [PowerToys](https://learn.microsoft.com/windows/powertoys/install) installed and Command Palette enabled.
- [Git](https://git-scm.com/downloads/win) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- A Visual Studio version that supports .NET 10, with the Windows application development workload, to deploy and debug the MSIX project.
- A GitHub account or access to your company's GitHub Enterprise host.

The project targets Windows 10 build 19041 or newer. Your PowerToys version may have newer Windows requirements, so check its installation guide too.

## Get the source and build

Open PowerShell:

```powershell
git clone https://github.com/baldbeardedbuilder/CmdPalGitHubExtension.git
Set-Location CmdPalGitHubExtension
dotnet build GitHubExtension\GitHubExtension.csproj -r win-x64
```

Always pass a Windows runtime identifier. MSIX packaging needs it. On ARM64, use `win-arm64` instead.

If you'll sign in to **github.com**, configure your OAuth app before building the copy you deploy. Enterprise token sign-in doesn't require that setup.

## Configure github.com sign-in

1. Open GitHub's [OAuth app registration](https://github.com/settings/applications/new).
2. Give the app a name you recognize and a homepage URL, such as this repository's URL.
3. Set **Authorization callback URL** to `http://127.0.0.1/callback`. The extension chooses a local port at sign-in; GitHub allows the loopback redirect to use a different port.
4. Register the app, then generate a client secret.
5. Set both values in the PowerShell session you'll build from:

```powershell
$env:GH_OAUTH_CLIENT_ID = "<your-client-id>"
$env:GH_OAUTH_CLIENT_SECRET = "<your-client-secret>"
dotnet build GitHubExtension\GitHubExtension.csproj -r win-x64
```

The placeholders above aren't credentials. Replace them locally, and don't paste real values into issues or commit them.

Use the `GH_` names above. GitHub reserves the `GITHUB_` prefix, and the build no longer reads the old OAuth variable names.

If you build in Visual Studio, close it first and launch it from that configured PowerShell session so it inherits the variables. Alternatively, create `GitHubExtension\oauth.local.props` locally:

```xml
<Project>
  <PropertyGroup>
    <GitHubOAuthClientId>YOUR_CLIENT_ID</GitHubOAuthClientId>
    <GitHubOAuthClientSecret>YOUR_CLIENT_SECRET</GitHubOAuthClientSecret>
  </PropertyGroup>
</Project>
```

That file is gitignored. The build embeds these OAuth values in the application, so treat this as personal development configuration, not a way to distribute a private secret. Your signed-in access token is stored separately in Windows Credential Locker.

## Deploy and open

1. Open `GitHubExtension.slnx` in Visual Studio.
2. Select **GitHubExtension** as the startup project and choose the architecture that matches your machine.
3. Deploy the project.
4. Open Command Palette and run **Reload Command Palette extensions**.
5. Type **GitHub** and open the extension.

Don't try to install an unsigned release package by turning off Windows security. The development deployment above is the current setup path.

Continue with [sign-in](/CmdPalGitHubExtension/getting-started/sign-in/). If a rebuild reports a locked executable, follow the [worktree-safe recovery steps](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/blob/main/CONTRIBUTING.md#recovering-from-a-locked-executable).
