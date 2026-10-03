param(
    [Parameter(Mandatory)]
    [string] $BundlePath,

    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$tenantId = $env:PARTNER_CENTER_TENANT_ID
$sellerId = $env:PARTNER_CENTER_SELLER_ID
$clientId = $env:PARTNER_CENTER_CLIENT_ID
$clientSecret = $env:PARTNER_CENTER_CLIENT_SECRET
$productId = $env:STORE_PRODUCT_ID
$storePublisher = $env:STORE_PUBLISHER_NAME
$packageIdentity = 'BaldBeardedBuilder.GitHubforCommandPalette'
$bundleName = [System.IO.Path]::GetFileName($BundlePath)
$baseUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$productId"
$headers = $null
$draftStates = @('InDraft', 'Draft')
$failureStates = @('CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'ReleaseFailed', 'PublishFailed', 'Canceled')

foreach ($name in @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_SELLER_ID', 'PARTNER_CENTER_CLIENT_ID', 'PARTNER_CENTER_CLIENT_SECRET', 'STORE_PRODUCT_ID', 'STORE_PUBLISHER_NAME')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required Microsoft Store setting '$name' is missing."
    }
}

function Get-StoreAccessToken {
    $body = @{
        grant_type    = 'client_credentials'
        client_id     = $clientId
        client_secret = $clientSecret
        resource      = 'https://manage.devcenter.microsoft.com'
    }

    try {
        $response = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenantId/oauth2/token" `
            -ContentType 'application/x-www-form-urlencoded' -Body $body
    }
    catch {
        throw 'Microsoft Store authentication failed. Check the Partner Center app permissions and tenant configuration.'
    }

    if ([string]::IsNullOrWhiteSpace($response.access_token)) {
        throw 'Microsoft Store authentication did not return an access token.'
    }

    return @{ Authorization = ('Bearer ' + [string]$response.access_token) }
}

function Get-SubmissionDetails([string] $SubmissionId) {
    Invoke-RestMethod -Method Get -Uri "$baseUri/submissions/$SubmissionId" -Headers $headers
}

function Get-SubmissionStatus([string] $SubmissionId) {
    Invoke-RestMethod -Method Get -Uri "$baseUri/submissions/$SubmissionId/status" -Headers $headers
}

function Assert-StoreApplication($Application) {
    if ($Application.id -ne $productId -or
        $Application.packageIdentityName -ne $packageIdentity -or
        $Application.publisherName -ne $storePublisher) {
        throw 'Partner Center product ID, package identity, or publisher does not match this extension. No Store draft was changed.'
    }
}

function Wait-ForSubmission([string] $SubmissionId) {
    $completeStates = @('Published', 'InStore')
    $deadline = [DateTime]::UtcNow.AddMinutes(10)

    while ([DateTime]::UtcNow -lt $deadline) {
        $status = Get-SubmissionStatus $SubmissionId
        Write-Host "Microsoft Store submission status: $($status.status)"
        if ($failureStates -contains $status.status) {
            throw "Microsoft Store submission entered failure state '$($status.status)'."
        }
        if ($completeStates -contains $status.status) {
            Write-Host "Microsoft Store reports this submission as $($status.status)."
            return
        }

        Start-Sleep -Seconds 30
    }

    $status = Get-SubmissionStatus $SubmissionId
    Write-Host "Microsoft Store submission is still '$($status.status)' after the 10-minute polling window."
    Write-Host 'The submission was accepted but is not confirmed as published. Re-run this tag after Store processing advances.'
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content $env:GITHUB_STEP_SUMMARY "Microsoft Store submission is pending in state **$($status.status)**. It is submitted, not confirmed as published. Re-run this tag after processing advances."
    }
}

$headers = Get-StoreAccessToken
$application = Invoke-RestMethod -Method Get -Uri $baseUri -Headers $headers
Assert-StoreApplication $application
$submissionId = $application.pendingApplicationSubmission.id
$submission = $null

if ($submissionId) {
    $submission = Get-SubmissionDetails $submissionId
    $matchingPackage = @($submission.applicationPackages | Where-Object { $_.fileName -eq $bundleName -and $_.fileStatus -ne 'PendingDelete' })
    if ($matchingPackage.Count -eq 0) {
        throw 'A Microsoft Store draft already exists without this release bundle. It was left unchanged; resolve or submit that draft before retrying.'
    }
    if ($matchingPackage.Count -gt 1) {
        throw 'The Microsoft Store draft contains duplicate entries for this release bundle. It was left unchanged.'
    }
}
else {
    $submissions = Invoke-RestMethod -Method Get -Uri "$baseUri/submissions" -Headers $headers
    foreach ($entry in @($submissions.value)) {
        if (-not $entry.id) {
            continue
        }

        $prior = Get-SubmissionDetails $entry.id
        $matchingPackage = @($prior.applicationPackages | Where-Object { $_.fileName -eq $bundleName -and $_.fileStatus -ne 'PendingDelete' })
        if ($matchingPackage.Count -gt 0) {
            $priorStatus = Get-SubmissionStatus $entry.id
            if ($priorStatus.status -notin $failureStates) {
                Write-Host "This release bundle is already submitted with status '$($priorStatus.status)'; no duplicate submission was created."
                Wait-ForSubmission $entry.id
                return
            }
        }
    }

    msstore publish $BundlePath -id $productId --noCommit | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "msstore publish --noCommit failed with exit code $LASTEXITCODE."
    }

    $application = Invoke-RestMethod -Method Get -Uri $baseUri -Headers $headers
    Assert-StoreApplication $application
    $submissionId = $application.pendingApplicationSubmission.id
    if (-not $submissionId) {
        throw 'Microsoft Store CLI returned success but no pending draft could be found.'
    }
    $submission = Get-SubmissionDetails $submissionId
    $matchingPackage = @($submission.applicationPackages | Where-Object { $_.fileName -eq $bundleName -and $_.fileStatus -ne 'PendingDelete' })
    if ($matchingPackage.Count -ne 1) {
        throw 'The Microsoft Store draft does not contain exactly one copy of this release bundle.'
    }
}

$activeSubmission = $submission.status -notin $draftStates -and $submission.status -notin $failureStates
if ($activeSubmission) {
    Write-Host "This release was already committed with status '$($submission.status)'; continuing to poll without creating another submission."
    Wait-ForSubmission $submissionId
    return
}

$changed = $false
$submission.applicationPackages | ForEach-Object {
    Write-Host "Store draft package: $($_.fileName), version '$($_.version)', status '$($_.fileStatus)'."
}
$pendingUploads = @($submission.applicationPackages | Where-Object { $_.fileStatus -eq 'PendingUpload' })
if (@($pendingUploads | Where-Object { $_.fileName -ne $bundleName }).Count -gt 0) {
    throw 'The Microsoft Store draft contains another pending upload. It was left unchanged.'
}

foreach ($package in $submission.applicationPackages) {
    if ($package.fileName -eq $bundleName) {
        if ($package.fileStatus -notin @('PendingUpload', 'Uploaded')) {
            throw "The release bundle has unsupported draft status '$($package.fileStatus)'."
        }
        continue
    }
    if ($package.fileStatus -eq 'Uploaded') {
        $package.fileStatus = 'PendingDelete'
        $changed = $true
    }
}

$survivingBundle = @($submission.applicationPackages | Where-Object { $_.fileName -eq $bundleName -and $_.fileStatus -ne 'PendingDelete' })
$survivingPackages = @($submission.applicationPackages | Where-Object { $_.fileStatus -ne 'PendingDelete' })
if ($survivingBundle.Count -ne 1 -or $survivingPackages.Count -eq 0) {
    throw 'Refusing to commit: the exact release bundle must survive and at least one package must remain.'
}

if ($changed) {
    $body = $submission | ConvertTo-Json -Depth 100
    Invoke-RestMethod -Method Put -Uri "$baseUri/submissions/$submissionId" -Headers $headers `
        -ContentType 'application/json' -Body $body | Out-Null
}

$status = Get-SubmissionStatus $submissionId
if ($status.status -notin $draftStates -and $status.status -notin $failureStates) {
    Wait-ForSubmission $submissionId
    return
}

Invoke-RestMethod -Method Post -Uri "$baseUri/submissions/$submissionId/commit" -Headers $headers `
    -ContentType 'application/json' | Out-Null
Write-Host "Committed Microsoft Store submission for $Version."
Wait-ForSubmission $submissionId
