# Releasing

## Stable tag flow

Push a stable tag in the form `vMAJOR.MINOR.PATCH` (for example, `v1.2.3`). The workflow rejects leading-zero versions, prereleases, and tags with another shape before it publishes anything. It stamps `Package.appxmanifest` to `MAJOR.MINOR.PATCH.0` in each job checkout. No version bump is committed to `main`, and MSIX package revision auto-increment is disabled.

The Release configuration enables Native AOT and trimming. Native AOT already emits a single executable, so `PublishSingleFile` is disabled to avoid an extra host executable colliding with the MSIX payload. Both publish profiles set ReadyToRun off and isolate the RID-specific publish and package output. Release tests run on Windows x64 without packaging; x64 and ARM64 packages are built on Windows x64 and native Windows ARM64 runners respectively. CI validates the package manifest and executable/assembly versions before release jobs can publish.

## Channel identity and upgrades

- GitHub release and WinGet use the sideload package identity `BaldBeardedBuilder.GitHubforCommandPalette` and the checked-in publisher `CN=Bald Bearded Builder LLC, O=Bald Bearded Builder LLC, L=Odenville, S=Alabama, C=US`. GitHub assets are signed MSIX packages; the architecture-named x64 and ARM64 assets provide WinGet's release URLs and hashes.
- Microsoft Store builds are unsigned and use the `STORE_PUBLISHER_NAME` registered for this product in Partner Center. The bundle contains exactly the release's x64 and ARM64 packages. The package name and Command Palette COM activation class remain the values in this repository's manifest; do not copy WeatherExtension IDs.
- Windows package identity includes the publisher. Packages signed under different publishers have different package family identities and do not upgrade each other. Choose the distribution channel deliberately; installing from one channel will not upgrade or replace an installation from another channel. Do not change the existing package name or COM activation class as part of release automation.

Before the first Microsoft Store submission, register this product in Partner Center, reserve its package identity and publisher, complete the listing and age-rating/privacy declarations, configure the API app with submission permissions, and complete the Store's initial certification and publication process. Submission acceptance or certification is not the same as public availability.

WinGet updates require an existing package manifest in `microsoft/winget-pkgs`. If the configured identifier is not present, submit the initial manifest through the WinGet community process and wait for it to merge before retrying a release tag. The workflow checks token authentication separately from the manifest lookup so an authentication error is not reported as a missing package.

## Protected environment configuration

Configure separate GitHub Actions environments named `release-signing`, `winget`, and `microsoft-store`. Protect the environments with required reviewers and branch/tag restrictions appropriate to production. Keep permissions limited to the jobs that need them.

### `release-signing`

Provide these environment values:

| Name | Type | Purpose |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | Secret | Federated Azure service principal client ID (preferred) |
| `AZURE_TENANT_ID` | Secret | Federated Azure tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Secret | Azure subscription for Artifact Signing |
| `AZURE_CREDS` | Secret, optional fallback | Azure Login service-principal JSON when federation is unavailable |
| `ARTIFACT_SIGNING_ENDPOINT` | Variable | Regional Artifact Signing endpoint |
| `ARTIFACT_SIGNING_ACCOUNT` | Variable | Artifact Signing account name |
| `ARTIFACT_SIGNING_PROFILE` | Variable | Certificate profile whose publisher matches the sideload manifest |

Configure the Azure federated credential for the repository's protected environment and grant only the required signing permissions. If federation cannot be used, provide `AZURE_CREDS` instead. Never add signing credentials to repository files.

### `winget`

Set `WINGET_TOKEN` to a valid classic GitHub PAT with `public_repo` permission to create a WinGet repository pull request. Ensure the PAT is authorized for organization SSO if required, and create a fork of `microsoft/winget-pkgs` under `baldbeardedbuilder` for the releaser to use. The package identifier is `BaldBeardedBuilder.GitHubforCommandPalette`; its first manifest must be onboarded before this automated update job can run successfully.

### `microsoft-store`

Set these protected environment secrets to the values registered for this extension, not WeatherExtension:

| Name | Purpose |
| --- | --- |
| `STORE_PUBLISHER_NAME` | Exact publisher string from Partner Center |
| `STORE_PRODUCT_ID` | This app's Partner Center product ID |
| `PARTNER_CENTER_TENANT_ID` | Partner Center API tenant |
| `PARTNER_CENTER_SELLER_ID` | Partner Center seller ID |
| `PARTNER_CENTER_CLIENT_ID` | Partner Center API application ID |
| `PARTNER_CENTER_CLIENT_SECRET` | Partner Center API application secret |

The workflow authenticates before uploading and checks the Partner Center application ID, package identity, and publisher before touching a draft. Microsoft Store submission jobs are serialized. Retries reuse the same versioned bundle name, skip an already-active submission, and refuse to modify a pending draft that does not contain that bundle. The workflow inspects inherited package statuses, marks only old `Uploaded` entries for deletion, preserves pending uploads, and verifies that the release bundle survives before commit. An upload whose parsed version is still empty is identified by its unique bundle filename, not by version metadata. If a partial run leaves an unrelated or unidentifiable draft, resolve it in Partner Center rather than deleting it automatically.

The Store submission step polls for up to ten minutes. A still-processing submission is reported as pending, not as published; re-run the same tag to resume checking. WinGet submission similarly creates a pull request and does not imply that the PR merged or the package is available.

## Packaging-only dry run

No production credentials or channel submissions are needed to check the release packaging path. Run the commands in [CONTRIBUTING.md](CONTRIBUTING.md#preparing-a-release) on the matching Windows architecture. The CI workflow also builds, unpacks, and validates both Release packages without signing or submitting them. Do not use a real release tag for a dry run.

Before announcing a release, install the signed x64 and ARM64 MSIX packages on supported hardware, verify fresh installation and in-channel upgrade behavior, launch Command Palette, reload extensions, and confirm the GitHub provider activates. Cross-channel upgrades are not supported because the publisher identity differs.
