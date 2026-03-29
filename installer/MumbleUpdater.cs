// Copyright The Mumble Developers. All rights reserved.
// Use of this source code is governed by a BSD-style license
// that can be found in the LICENSE file at the root of the
// Mumble source tree or at <https://www.mumble.info/LICENSE>.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MumbleUpdater
{
    /// <summary>
    /// Minimal representation of a GitHub release returned by the releases API.
    /// </summary>
    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    /// <summary>
    /// Minimal representation of a single release asset.
    /// </summary>
    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    internal static class Program
    {
        // GitHub repository to query for releases.
        // This targets the fork that ships the modern-mumble distribution.
        private const string Owner = "MarwanSummakieh";
        private const string Repo  = "modern-mumble";

        // Registry path where Mumble stores its installed version on Windows.
        private const string MumbleRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{D269FC55-4F2C-4285-9AA9-4D034AF305C4}";

        // Pattern that matches the Windows installer asset shipped with every release,
        // e.g. "mumble_client-1.5.735-x64.exe".
        private static readonly Regex InstallerAssetPattern =
            new Regex(@"mumble[_\-]client[_\-].+\-x64\.exe$", RegexOptions.IgnoreCase);

        private static async Task<int> Main()
        {
            Console.WriteLine("=== Mumble Updater ===");
            Console.WriteLine();

            using var httpClient = CreateHttpClient();

            // ----------------------------------------------------------------
            // 1. Fetch the latest release from GitHub.
            // ----------------------------------------------------------------
            GitHubRelease? latest;
            try
            {
                latest = await FetchLatestRelease(httpClient);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: Could not reach GitHub API: {ex.Message}");
                return 1;
            }

            if (latest is null)
            {
                Console.Error.WriteLine("ERROR: No release information returned by the GitHub API.");
                return 1;
            }

            Console.WriteLine($"Latest release : {latest.TagName}");

            // ----------------------------------------------------------------
            // 2. Determine the currently installed version (best-effort).
            // ----------------------------------------------------------------
            string installedVersion = GetInstalledVersion();
            if (!string.IsNullOrEmpty(installedVersion))
            {
                Console.WriteLine($"Installed      : {installedVersion}");
            }
            else
            {
                Console.WriteLine("Installed      : (not detected)");
            }

            // Strip the leading "v" from the tag so we can compare versions.
            string latestVersion = latest.TagName.TrimStart('v');

            if (!string.IsNullOrEmpty(installedVersion) &&
                IsUpToDate(installedVersion, latestVersion))
            {
                Console.WriteLine();
                Console.WriteLine("Mumble is already up to date.");
                Pause();
                return 0;
            }

            // ----------------------------------------------------------------
            // 3. Find the Windows x64 installer asset.
            // ----------------------------------------------------------------
            GitHubAsset? installerAsset = FindInstallerAsset(latest);
            if (installerAsset is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: No Windows x64 installer asset found for release {latest.TagName}.");
                Console.Error.WriteLine($"       Visit {latest.HtmlUrl} to download manually.");
                Pause();
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"Update available: {latestVersion}");
            Console.Write("Download and install now? [Y/n] ");
            string? answer = Console.ReadLine();
            if (!string.IsNullOrEmpty(answer) &&
                !answer.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Update cancelled.");
                return 0;
            }

            // ----------------------------------------------------------------
            // 4. Download the installer to a temp file.
            // ----------------------------------------------------------------
            string tempPath = Path.Combine(Path.GetTempPath(), installerAsset.Name);
            Console.WriteLine();
            Console.WriteLine($"Downloading {installerAsset.Name} ...");

            try
            {
                await DownloadWithProgress(httpClient, installerAsset.BrowserDownloadUrl,
                                           tempPath, installerAsset.Size,
                                           CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"ERROR: Download failed: {ex.Message}");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Download complete.");

            // ----------------------------------------------------------------
            // 5. Launch the installer.
            // ----------------------------------------------------------------
            Console.WriteLine("Launching installer …");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = tempPath,
                    UseShellExecute = true,
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: Could not launch installer: {ex.Message}");
                Console.Error.WriteLine($"       Installer saved to: {tempPath}");
                Pause();
                return 1;
            }

            return 0;
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
            };
            var client = new HttpClient(handler);
            // GitHub API requires a User-Agent header.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MumbleUpdater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static async Task<GitHubRelease?> FetchLatestRelease(HttpClient client)
        {
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            return await client.GetFromJsonAsync<GitHubRelease>(url);
        }

        private static string GetInstalledVersion()
        {
            try
            {
                using RegistryKey? key =
                    Registry.LocalMachine.OpenSubKey(MumbleRegistryPath);

                string? displayVersion = key?.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(displayVersion))
                    return displayVersion;
            }
            catch
            {
                // Registry access can fail on restricted accounts or non-Windows
                // environments; return an empty string so the caller falls back to
                // an "update available" assumption.
            }

            return string.Empty;
        }

        private static bool IsUpToDate(string installed, string latest)
        {
            if (Version.TryParse(installed, out Version? iv) &&
                Version.TryParse(latest,    out Version? lv))
            {
                return iv >= lv;
            }

            // Fall back to string equality when the version strings cannot be
            // parsed as System.Version (e.g., pre-release tags).
            return string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
        }

        private static GitHubAsset? FindInstallerAsset(GitHubRelease release)
        {
            foreach (var asset in release.Assets)
            {
                if (InstallerAssetPattern.IsMatch(asset.Name))
                    return asset;
            }
            return null;
        }

        private static async Task DownloadWithProgress(
            HttpClient client, string url, string destPath, long totalBytes,
            CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                                                       cancellationToken);
            response.EnsureSuccessStatusCode();

            // Prefer the content-length reported by the server over the size
            // stored in the asset metadata.
            long length = response.Content.Headers.ContentLength ?? totalBytes;

            using var src  = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                                            FileShare.None, 81920, useAsync: true);

            byte[] buffer    = new byte[81920];
            long   received  = 0;
            int    bytesRead;

            while ((bytesRead = await src.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                received += bytesRead;

                if (length > 0)
                {
                    int pct = (int)(received * 100L / length);
                    Console.Write($"\r  {pct,3}%  ({FormatBytes(received)} / {FormatBytes(length)})  ");
                }
                else
                {
                    Console.Write($"\r  {FormatBytes(received)} downloaded …");
                }
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_048_576)
                return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024)
                return $"{bytes / 1_024.0:F1} KB";
            return $"{bytes} B";
        }

        private static void Pause()
        {
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.Write("Press any key to exit …");
                Console.ReadKey(intercept: true);
            }
        }
    }
}
