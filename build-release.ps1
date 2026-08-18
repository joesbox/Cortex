param(
	[string]$ProjectPath = ".\Cortex.csproj",
	[string]$Configuration = "Release",
	[bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFullPath = Join-Path $repoRoot $ProjectPath

if (-not (Test-Path -Path $projectFullPath)) {
	throw "Project file not found: $projectFullPath"
}

$publishRoot = Join-Path $repoRoot "artifacts\publish"
$releaseRoot = Join-Path $repoRoot "artifacts\release"
$innoScriptPath = Join-Path $repoRoot "InnoSetup Script\CortexSetup.iss"
$innoOutputDir = Join-Path $repoRoot "InnoSetup Script\Output"
$windowsSetupExe = Join-Path $innoOutputDir "CortexSetup.exe"
$windowsSetupZip = Join-Path $innoOutputDir "CortexSetupWindows.zip"
$windowsPublishDir = Join-Path $publishRoot "win-x64"

[xml]$projectXml = Get-Content -Path $projectFullPath
$appVersion = $projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) {
	throw "Unable to read <Version> from $projectFullPath"
}

$rids = @(
	"win-x64",
	"linux-x64",
	"osx-x64",
	"osx-arm64"
)

if (-not (Test-Path -Path $releaseRoot)) {
	New-Item -ItemType Directory -Path $releaseRoot | Out-Null
}

Write-Host "Restoring project..."
& dotnet restore $projectFullPath

foreach ($rid in $rids) {
	$ridPublishDir = Join-Path $publishRoot $rid

	if (Test-Path -Path $ridPublishDir) {
		Remove-Item -Path $ridPublishDir -Recurse -Force
	}

	New-Item -ItemType Directory -Path $ridPublishDir | Out-Null

	Write-Host "Publishing $rid (self-contained=$SelfContained)..."
	& dotnet publish $projectFullPath -c $Configuration -r $rid --self-contained $SelfContained -o $ridPublishDir

	if ($LASTEXITCODE -ne 0) {
		throw "dotnet publish failed for RID '$rid'."
	}
}

Write-Host "Packing Linux and macOS release archives..."
$archives = @(
	@{ Rid = "linux-x64"; Name = "linux-x64.tar.gz" },
	@{ Rid = "osx-x64"; Name = "osx-x64.tar.gz" },
	@{ Rid = "osx-arm64"; Name = "osx-arm64.tar.gz" }
)

foreach ($archive in $archives) {
	$rid = $archive.Rid
	$archiveName = $archive.Name
	$sourceDir = Join-Path $publishRoot $rid
	$targetArchive = Join-Path $releaseRoot $archiveName

	if (Test-Path -Path $targetArchive) {
		Remove-Item -Path $targetArchive -Force
	}

	& tar -czf $targetArchive -C $sourceDir .

	if ($LASTEXITCODE -ne 0) {
		throw "tar failed for '$rid'."
	}
}

if (-not (Test-Path -Path $innoScriptPath)) {
	throw "Inno Setup script not found: $innoScriptPath"
}

Write-Host "Compiling Windows installer from Inno Setup script..."
$isccCommand = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
$iscc = $null
if ($isccCommand) {
	$iscc = $isccCommand.Source
}
if (-not $iscc) {
	$innoCandidates = @(
		"${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
		"${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
	)

	foreach ($candidate in $innoCandidates) {
		if (Test-Path -Path $candidate) {
			$iscc = $candidate
			break
		}
	}
}

if (-not $iscc) {
	throw "ISCC.exe not found. Install Inno Setup 6 or add ISCC.exe to PATH."
}

$isccArgs = @(
	"/DMyAppVersion=$appVersion",
	"/DMyProjectRoot=$repoRoot",
	"/DMyPublishDir=$windowsPublishDir",
	$innoScriptPath
)

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
	throw "Inno Setup compilation failed."
}

if (-not (Test-Path -Path $windowsSetupExe)) {
	throw "Expected installer not found: $windowsSetupExe"
}

Write-Host "Creating Windows installer zip..."
if (Test-Path -Path $windowsSetupZip) {
	Remove-Item -Path $windowsSetupZip -Force
}
Compress-Archive -Path $windowsSetupExe -DestinationPath $windowsSetupZip -Force

Copy-Item -Path $windowsSetupZip -Destination (Join-Path $releaseRoot "CortexSetupWindows.zip") -Force

Write-Host "Release artifacts created:"
Get-ChildItem -Path $releaseRoot -File | Select-Object -ExpandProperty FullName