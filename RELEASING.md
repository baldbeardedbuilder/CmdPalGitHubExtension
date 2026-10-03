# Releasing

Releases are published by [`.github/workflows/release.yml`](.github/workflows/release.yml) when a version tag matching `v*` is pushed. The workflow builds and tests the application, creates signed MSIX packages for GitHub and WinGet, and submits a multi-architecture package to the Microsoft Store.

## Supported publishing configuration

Release packages use .NET 10 Native AOT, trimming, and self-contained deployment for `win-x64` and `win-arm64`. Pass the matching publish profile to generate the MSIX. Both profiles disable single-file publishing and leave signing to the release workflow. Debug builds are for development, not release compatibility checks.

You can validate unsigned packages without release credentials from a Visual Studio Developer PowerShell with the C++ build tools and Windows SDK available:

```powershell
dotnet build GitHubExtension\GitHubExtension.csproj -r win-x64
dotnet test GitHubExtension.Tests\GitHubExtension.Tests.csproj -r win-x64
dotnet test GitHubExtension.Tests\GitHubExtension.Tests.csproj -c Release -r win-x64
dotnet publish GitHubExtension\GitHubExtension.csproj -c Release -r win-x64 /p:PublishProfile=win-x64 /p:AppxPackageSigningEnabled=false
dotnet publish GitHubExtension\GitHubExtension.csproj -c Release -r win-arm64 /p:PublishProfile=win-arm64 /p:AppxPackageSigningEnabled=false
& .\GitHubExtension.JsonSmoke\Verify-NativePackage.ps1 -RuntimeIdentifier win-x64
& .\GitHubExtension.JsonSmoke\Verify-NativePackage.ps1 -RuntimeIdentifier win-arm64
dotnet publish GitHubExtension.JsonSmoke\GitHubExtension.JsonSmoke.csproj -c Release -r win-x64 -o GitHubExtension.JsonSmoke\bin\native
& .\GitHubExtension.JsonSmoke\bin\native\GitHubExtension.JsonSmoke.exe
```

MSIX files are written under `GitHubExtension\AppPackages`. If native linking cannot locate Visual Studio tools, initialize the installed Visual Studio developer environment and ensure its Installer directory (containing `vswhere.exe`) is on `PATH`. On ARM64 development machines, select the matching target architecture when initializing that environment.

PR CI runs Debug and Release tests, publishes unsigned packages for both architectures, verifies that each MSIX contains a native executable of the expected architecture, and executes the x64 native JSON smoke check. The smoke project links the production JSON metadata, Codespaces and Agent Task cards, workflow cards, and their models. It verifies escaping, optional branch/option omission, numeric repository IDs, action payloads, and GraphQL variable types with reflection serialization disabled. It never makes network requests or creates Codespaces, agent tasks, merges, or workflow reruns.

The investigation for #67 reproduced `IL2026` and `IL3050` errors in a Release build, including Codespaces cards and creation requests. Generated metadata now covers these paths and the related Agent Tasks, GraphQL, merge, and workflow JSON. Keep the analyzers enabled rather than suppressing these failures or disabling AOT.

The native smoke check validates card JSON, not the Command Palette renderer. Before distributing an installed release, open the Create Codespace form in Command Palette and confirm its inputs, optional branch, validation message, and actions render correctly. Leave the branch blank and use a malformed repository to check local validation without sending a creation request. Do not submit a valid creation request just to verify rendering. Unsigned publishing does not verify package signing, Store submission, ARM64 execution, or installed UI behavior.

## Configure GitHub Actions secrets

Add these as repository secrets under **Settings > Secrets and variables > Actions**. The workflow does not declare a GitHub Actions environment, so repository secrets or accessible organization secrets are appropriate.

| Secret | Purpose and value |
| --- | --- |
| `AZURE_CREDS` | JSON credentials consumed by `azure/login`. Use the service principal JSON format with `clientId`, `clientSecret`, `subscriptionId`, and `tenantId`. The principal must be authorized to use the configured Azure Artifact Signing account and certificate profile. |
| `STORE_PUBLISHER_NAME` | Exact publisher identity from the Microsoft Store listing, for example `CN=...`. It is written into the Store package manifest. |
| `PARTNER_CENTER_TENANT_ID` | Microsoft Entra tenant ID for the Partner Center application. |
| `PARTNER_CENTER_SELLER_ID` | Partner Center seller ID for the publisher account. |
| `PARTNER_CENTER_CLIENT_ID` | Client ID of the Entra application used for Store submission. |
| `PARTNER_CENTER_CLIENT_SECRET` | Client secret for that Entra application. Configure the application and Partner Center account for authenticated submission access. |
| `STORE_PRODUCT_ID` | Product/application ID of the app in Partner Center. |
| `WINGET_TOKEN` | GitHub **classic** personal access token with the `public_repo` scope, used to submit the package manifest to `microsoft/winget-pkgs`. Ensure it is not expired, authorize it for SSO if required, and save it without surrounding whitespace. |

`GITHUB_TOKEN` is provided automatically by GitHub Actions. The workflow grants it `contents: write` to create the GitHub Release and upload its MSIX assets. Do not create a separate secret for it.

These are release-pipeline credentials. The application's optional `GITHUB_OAUTH_CLIENT_ID` and `GITHUB_OAUTH_CLIENT_SECRET` are separate runtime/build configuration and are not referenced by this workflow.

## Create a release

1. Merge the intended release changes into the release branch (normally `main`) and confirm CI is green.
2. Create and push a new version tag. Use `vMAJOR.MINOR.PATCH`, such as `v1.2.3`; the workflow converts this to the four-part package version `1.2.3.0`. A four-part tag such as `v1.2.3.4` is passed through unchanged.

   ```powershell
   git tag v1.2.3
   git push origin v1.2.3
   ```

3. Follow the **Release to Microsoft Store** workflow in the Actions tab. The build-and-test job gates the publication jobs; any missing/invalid secret or failed publication step fails the corresponding job.
4. Verify the GitHub Release assets, WinGet submission, and Microsoft Store submission after the workflow completes. Store certification and public availability may continue after the workflow reports that the submission was accepted.

## What the workflow publishes

1. **Build and test:** extracts the version from the tag, updates the package manifest version, builds x64 and ARM64, and runs the x64 test suite.
2. **GitHub Release and WinGet:** builds x64 and ARM64 sideload MSIX packages, signs and verifies them with Azure Artifact Signing, creates a GitHub Release with generated notes, and uploads both MSIX files. The WinGet job validates `WINGET_TOKEN` and submits the release version and matching MSIX assets to the configured WinGet package identifier.
3. **Microsoft Store:** rebuilds both architectures with the Store publisher identity, bundles them into one multi-architecture `.msixbundle`, creates a Partner Center draft, removes inherited stale packages, then commits the submission. The workflow checks the submission status for up to ten minutes; Partner Center can continue processing it afterward.

The workflow uploads intermediate artifacts named `msix-packages` and `msix-store-packages`, retained for seven days.

## Recovery notes

- Push a new version tag for a new release. Do not reuse or move a tag after publication has started.
- The GitHub Release is created before Store and WinGet publication finish. If a later job fails, the release may already exist and contain its signed MSIX assets. Inspect the failed job and the existing release before retrying; rerunning the workflow may stop when `gh release create` encounters that existing release.
- A Store submission reported as accepted or in certification is not necessarily live in the Store yet. Check Partner Center for certification and publishing status.

## Workflow considerations

- The workflow accepts every tag matching `v*`, but it does not validate that the tag is a valid three- or four-part numeric version. Use the formats above; malformed tags can fail during manifest update or packaging.
- GitHub Release creation is not currently idempotent, so a rerun after that step can fail on the existing release. Handle reruns by checking and repairing the already-created release/submissions rather than moving the tag.
