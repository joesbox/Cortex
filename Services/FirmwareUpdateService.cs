using Cortex.Models;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.Services
{
    public sealed class FirmwareUpdateService
    {
        private const string LatestFirmwareUrl = "https://updates.mannelectronics.uk/pdm/latest";
        private const string FirmwareGitHubRepository = "https://github.com/joesbox/SynapsePDM";
        private const string FirmwareGitHubApiRepository = "https://api.github.com/repos/joesbox/SynapsePDM";
        private const int UploadChunkSize = 512;

        private static readonly HttpClient HttpClient = new HttpClient();

        public async Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(LatestFirmwareUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<LatestFirmwareResponse>(responseStream, cancellationToken: cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Version) || string.IsNullOrWhiteSpace(payload.Firmware) || string.IsNullOrWhiteSpace(payload.Signature))
            {
                return null;
            }

            string? gitHubUrl = await TryGetGitHubTagUrlAsync(payload.Version, cancellationToken);
            return new FirmwareReleaseInfo
            {
                Version = payload.Version,
                FirmwareUrl = payload.Firmware,
                SignatureUrl = payload.Signature,
                GitHubUrl = gitHubUrl,
                Size = payload.Size,
            };
        }

        public async Task<FirmwareReleaseInfo?> GetAvailableReleaseAsync(string? currentVersion, CancellationToken cancellationToken)
        {
            FirmwareReleaseInfo? latestRelease = await GetLatestReleaseAsync(cancellationToken);
            if (latestRelease == null || !IsVersionNewer(latestRelease.Version, currentVersion))
            {
                return null;
            }

            return latestRelease;
        }

        public static bool IsVersionNewerThanCurrent(string? latestVersion, string? currentVersion)
        {
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return false;
            }

            return IsVersionNewer(latestVersion, currentVersion);
        }

        public async Task InstallReleaseAsync(
            FirmwareReleaseInfo release,
            SerialPortService serialPortService,
            IProgress<FirmwareUpdateProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new FirmwareUpdateProgressInfo
            {
                StatusMessage = $"Downloading firmware {release.Version}...",
                ProgressPercent = 5,
                CanCancel = true,
            });

            byte[] firmwareBytes = await DownloadBytesWithProgressAsync(release.FirmwareUrl, 5, 35, progress, cancellationToken, "Downloading firmware...");
            byte[] signatureBytes = await DownloadBytesWithProgressAsync(release.SignatureUrl, 35, 45, progress, cancellationToken, "Downloading signature...");

            string sha256Hex = Convert.ToHexString(SHA256.HashData(firmwareBytes)).ToLowerInvariant();
            byte[] sha256Bytes = Encoding.ASCII.GetBytes(sha256Hex);

            await InstallPackageAsync(
                serialPortService,
                firmwareBytes,
                sha256Bytes,
                signatureBytes,
                progress,
                cancellationToken);
        }

        public async Task InstallLocalFilesAsync(
            LocalFirmwareUpdateSelection selection,
            SerialPortService serialPortService,
            IProgress<FirmwareUpdateProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            progress.Report(new FirmwareUpdateProgressInfo
            {
                StatusMessage = "Loading local firmware package...",
                ProgressPercent = 5,
                CanCancel = true,
            });

            LocalFirmwarePackage package = await ReadLocalFirmwarePackageAsync(selection, progress, cancellationToken);

            await InstallPackageAsync(
                serialPortService,
                package.FirmwareBytes,
                package.Sha256Bytes,
                package.SignatureBytes,
                progress,
                cancellationToken);
        }

        private static async Task<LocalFirmwarePackage> ReadLocalFirmwarePackageAsync(
            LocalFirmwareUpdateSelection selection,
            IProgress<FirmwareUpdateProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(selection.PackagePath) || !File.Exists(selection.PackagePath))
            {
                throw new InvalidOperationException("The selected firmware package couldn't be found. Choose the zip file again.");
            }

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(selection.PackagePath);

                var packageEntries = archive.Entries
                    .Where(entry => !string.IsNullOrEmpty(entry.Name))
                    .Select(entry => new
                    {
                        Entry = entry,
                        NormalizedBase = GetNormalizedBasePath(entry.FullName),
                        Extension = Path.GetExtension(entry.Name),
                    })
                    .GroupBy(item => item.NormalizedBase, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        BasePath = group.Key,
                        Firmware = group.FirstOrDefault(item => item.Extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))?.Entry,
                        Hash = group.FirstOrDefault(item => item.Extension.Equals(".sha256", StringComparison.OrdinalIgnoreCase))?.Entry,
                        Signature = group.FirstOrDefault(item => item.Extension.Equals(".sig", StringComparison.OrdinalIgnoreCase))?.Entry,
                    })
                    .Where(group => group.Firmware != null && group.Hash != null && group.Signature != null)
                    .ToList();

                if (packageEntries.Count == 0)
                {
                    throw new InvalidOperationException("The selected zip file doesn't contain a matching firmware package. Include .bin, .sha256, and .sig files with the same filename.");
                }

                if (packageEntries.Count > 1)
                {
                    throw new InvalidOperationException("The selected zip file contains multiple firmware packages. Keep only one matching .bin, .sha256, and .sig set in the zip.");
                }

                var packageEntry = packageEntries[0];
                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Loading firmware from zip package...",
                    ProgressPercent = 10,
                    CanCancel = true,
                });

                byte[] firmwareBytes = await ReadZipEntryBytesAsync(packageEntry.Firmware!, "firmware binary", cancellationToken);

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Loading hash from zip package...",
                    ProgressPercent = 18,
                    CanCancel = true,
                });

                string sha256Text = await ReadZipEntryTextAsync(packageEntry.Hash!, "SHA256 file", cancellationToken);

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Loading signature from zip package...",
                    ProgressPercent = 25,
                    CanCancel = true,
                });

                byte[] signatureBytes = await ReadZipEntryBytesAsync(packageEntry.Signature!, "signature file", cancellationToken);

                return new LocalFirmwarePackage
                {
                    FirmwareBytes = firmwareBytes,
                    Sha256Bytes = Encoding.ASCII.GetBytes(sha256Text.Trim()),
                    SignatureBytes = signatureBytes,
                };
            }
            catch (InvalidDataException)
            {
                throw new InvalidOperationException("The selected file isn't a valid firmware update zip package.");
            }
            catch (IOException)
            {
                throw new InvalidOperationException("The selected firmware package couldn't be opened. Check that the zip file isn't open in another program and try again.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The selected firmware package couldn't be opened. Check that you have access to the zip file and try again.");
            }
        }

        private static string GetNormalizedBasePath(string fullName)
        {
            string normalizedPath = fullName.Replace('\\', '/');
            string extension = Path.GetExtension(normalizedPath);
            return normalizedPath[..^extension.Length];
        }

        private static async Task<byte[]> ReadZipEntryBytesAsync(ZipArchiveEntry entry, string description, CancellationToken cancellationToken)
        {
            try
            {
                await using Stream stream = entry.Open();
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream, cancellationToken);
                return memoryStream.ToArray();
            }
            catch (InvalidDataException)
            {
                throw new InvalidOperationException($"The {description} inside the zip package is corrupt.");
            }
        }

        private static async Task<string> ReadZipEntryTextAsync(ZipArchiveEntry entry, string description, CancellationToken cancellationToken)
        {
            byte[] bytes = await ReadZipEntryBytesAsync(entry, description, cancellationToken);
            return Encoding.UTF8.GetString(bytes);
        }

        private static async Task InstallPackageAsync(
            SerialPortService serialPortService,
            byte[] firmwareBytes,
            byte[] sha256Bytes,
            byte[] signatureBytes,
            IProgress<FirmwareUpdateProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            bool expectControllerRestart = false;
            bool controllerRestartAccepted = false;
            if (firmwareBytes.Length == 0)
            {
                throw new InvalidOperationException("Firmware file is empty.");
            }

            if (sha256Bytes.Length == 0)
            {
                throw new InvalidOperationException("SHA256 file is empty.");
            }

            if (signatureBytes.Length == 0)
            {
                throw new InvalidOperationException("Signature file is empty.");
            }

            serialPortService.BeginFirmwareUpdateSession();
            try
            {
                await UploadAssetAsync(serialPortService, (byte)0, firmwareBytes, 45, 72, progress, cancellationToken, "Transferring firmware to controller...");
                await UploadAssetAsync(serialPortService, (byte)1, sha256Bytes, 72, 78, progress, cancellationToken, "Transferring hash to controller...");
                await UploadAssetAsync(serialPortService, (byte)2, signatureBytes, 78, 84, progress, cancellationToken, "Transferring signature to controller...");

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Verifying firmware on controller...",
                    ProgressPercent = 88,
                    CanCancel = false,
                });

                bool installAccepted = await serialPortService.InstallFirmwareAsync(30000);
                if (!installAccepted)
                {
                    string diagnostic = await GetFirmwareDiagnosticSuffixAsync(serialPortService);
                    throw new InvalidOperationException($"Controller rejected the firmware install request{diagnostic}");
                }

                expectControllerRestart = true;
                controllerRestartAccepted = true;

                serialPortService.EndFirmwareUpdateSession(expectControllerRestart: true);
                expectControllerRestart = false;

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Controller restarting...",
                    ProgressPercent = 92,
                    CanCancel = false,
                });

                bool reconnected = await serialPortService.WaitForControllerReconnectAsync(20000, 250);
                if (!reconnected)
                {
                    throw new InvalidOperationException("Controller did not re-establish communications after the firmware update.");
                }

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Controller reconnected. Refreshing status...",
                    ProgressPercent = 98,
                    CanCancel = false,
                });

                await Task.Delay(500, cancellationToken);

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = "Firmware update complete.",
                    ProgressPercent = 100,
                    CanCancel = false,
                });
            }
            catch
            {
                if (!controllerRestartAccepted)
                {
                    await serialPortService.CancelFirmwareUploadAsync(2000);
                }

                throw;
            }
            finally
            {
                if (expectControllerRestart)
                {
                    serialPortService.EndFirmwareUpdateSession(expectControllerRestart);
                }
            }
        }

        private static async Task UploadAssetAsync(
            SerialPortService serialPortService,
            byte assetType,
            byte[] payload,
            double progressStart,
            double progressEnd,
            IProgress<FirmwareUpdateProgressInfo> progress,
            CancellationToken cancellationToken,
            string statusMessage)
        {
            if (!await serialPortService.BeginFirmwareUploadAsync(assetType, payload.Length, 5000))
            {
                string diagnostic = await GetFirmwareDiagnosticSuffixAsync(serialPortService);
                throw new InvalidOperationException($"Controller refused firmware upload start{diagnostic}");
            }

            int bytesSent = 0;
            while (bytesSent < payload.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkLength = Math.Min(UploadChunkSize, payload.Length - bytesSent);
                byte[] chunk = new byte[chunkLength];
                Buffer.BlockCopy(payload, bytesSent, chunk, 0, chunkLength);

                if (!await serialPortService.SendFirmwareChunkAsync(chunk, chunkLength, 5000))
                {
                    string diagnostic = await GetFirmwareDiagnosticSuffixAsync(serialPortService);
                    throw new InvalidOperationException($"Controller rejected a firmware upload chunk{diagnostic}");
                }

                bytesSent += chunkLength;
                double fractionComplete = payload.Length == 0 ? 1.0 : (double)bytesSent / payload.Length;
                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = statusMessage,
                    ProgressPercent = progressStart + ((progressEnd - progressStart) * fractionComplete),
                    CanCancel = true,
                });
            }

            if (!await serialPortService.FinishFirmwareUploadAsync(5000))
            {
                string diagnostic = await GetFirmwareDiagnosticSuffixAsync(serialPortService);
                throw new InvalidOperationException($"Controller did not finalize the uploaded firmware asset{diagnostic}");
            }
        }

        private static async Task<string> GetFirmwareDiagnosticSuffixAsync(SerialPortService serialPortService)
        {
            string? diagnostic = await serialPortService.RequestFirmwareDiagnosticAsync(2000);
            if (string.IsNullOrWhiteSpace(diagnostic))
            {
                return string.Empty;
            }

            return $": {diagnostic.Trim()}";
        }

        private static async Task<byte[]> DownloadBytesWithProgressAsync(
            string url,
            double progressStart,
            double progressEnd,
            IProgress<FirmwareUpdateProgressInfo> progress,
            CancellationToken cancellationToken,
            string statusMessage)
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new System.IO.MemoryStream();

            byte[] buffer = new byte[8192];
            long totalRead = 0;
            while (true)
            {
                int bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                double fractionComplete = contentLength.HasValue && contentLength.Value > 0
                    ? Math.Clamp((double)totalRead / contentLength.Value, 0.0, 1.0)
                    : 0.0;

                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = statusMessage,
                    ProgressPercent = progressStart + ((progressEnd - progressStart) * fractionComplete),
                    CanCancel = true,
                });
            }

            if (!contentLength.HasValue)
            {
                progress.Report(new FirmwareUpdateProgressInfo
                {
                    StatusMessage = statusMessage,
                    ProgressPercent = progressEnd,
                    CanCancel = true,
                });
            }

            return memoryStream.ToArray();
        }

        private static bool IsVersionNewer(string? candidateVersion, string? currentVersion)
        {
            if (string.IsNullOrWhiteSpace(candidateVersion))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                return true;
            }

            if (TryParseVersion(candidateVersion, out Version? candidate) && TryParseVersion(currentVersion, out Version? current))
            {
                return candidate > current;
            }

            return !string.Equals(candidateVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseVersion(string versionText, out Version? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return false;
            }

            string sanitized = versionText.Trim();
            if (sanitized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = sanitized[1..];
            }

            return Version.TryParse(sanitized, out version);
        }

        private static async Task<string?> TryGetGitHubTagUrlAsync(string version, CancellationToken cancellationToken)
        {
            foreach (string candidateTag in GetGitHubTagCandidates(version))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{FirmwareGitHubApiRepository}/git/ref/tags/{Uri.EscapeDataString(candidateTag)}");
                    request.Headers.UserAgent.ParseAdd("Cortex");

                    using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return $"{FirmwareGitHubRepository}/tree/{Uri.EscapeDataString(candidateTag)}";
                    }

                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        return null;
                    }
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string[] GetGitHubTagCandidates(string version)
        {
            string trimmedVersion = version.Trim();
            if (string.IsNullOrWhiteSpace(trimmedVersion))
            {
                return [];
            }

            if (trimmedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                return [trimmedVersion, trimmedVersion[1..]];
            }

            return [trimmedVersion, $"v{trimmedVersion}"];
        }

        private sealed class LatestFirmwareResponse
        {
            [JsonPropertyName("firmware")]
            public string Firmware { get; set; } = string.Empty;

            [JsonPropertyName("signature")]
            public string Signature { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("version")]
            public string Version { get; set; } = string.Empty;
        }

        private sealed class LocalFirmwarePackage
        {
            public byte[] FirmwareBytes { get; set; } = Array.Empty<byte>();

            public byte[] Sha256Bytes { get; set; } = Array.Empty<byte>();

            public byte[] SignatureBytes { get; set; } = Array.Empty<byte>();
        }
    }
}