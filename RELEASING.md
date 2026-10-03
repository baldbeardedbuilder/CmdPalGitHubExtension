# Releasing

Releases are published by [`.github/workflows/release.yml`](.github/workflows/release.yml) when a version tag matching `v*` is pushed. The workflow builds and tests the application, creates signed MSIX packages for GitHub and WinGet, and submits a multi-architecture package to the Microsoft Store.

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
