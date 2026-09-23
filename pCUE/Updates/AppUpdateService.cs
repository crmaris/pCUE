using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace pCUE
{
    /// <summary>Outcome of an update check.</summary>
    public enum UpdateCheckState
    {
        /// <summary>Never checked in this session.</summary>
        Unknown,

        /// <summary>The installed copy is current.</summary>
        UpToDate,

        /// <summary>A newer build is published.</summary>
        UpdateAvailable,

        /// <summary>The check itself failed (offline, bad manifest, ...).</summary>
        Failed,
    }

    /// <summary>What a check found. Download fields are only set when an update is available.</summary>
    public sealed class AppUpdateInfo
    {
        public UpdateCheckState State { get; set; }
        public string InstalledVersion { get; set; }
        public string AvailableVersion { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public string Message { get; set; }

        public static AppUpdateInfo Failure(string installed, string message)
        {
            return new AppUpdateInfo
            {
                State = UpdateCheckState.Failed,
                InstalledVersion = installed,
                Message = message,
            };
        }
    }

    /// <summary>
    /// In-app updater for pCUE, mirroring the Powenetics V2/V3 component updater.
    ///
    /// It reads a JSON manifest over HTTPS and compares the published version with the version
    /// stamped into the running executable. Security rules, kept deliberately strict:
    ///   1. HTTPS only - plain HTTP is rejected for both the manifest and the download.
    ///   2. The manifest MUST supply a sha256; a download whose hash does not match is deleted.
    ///   3. A download is NEVER executed automatically. The caller confirms with the user and
    ///      launches the installer, because a running app cannot overwrite its own files.
    ///   4. Checking never installs anything by itself.
    ///
    /// TRUST: rules 1-2 are INTEGRITY, not authenticity. A compromised manifest repo serving a
    /// matching url+sha256 would be accepted, then launched elevated (pCUE runs as admin) after
    /// the user's two confirms. There is no Authenticode or out-of-band signature check.
    ///
    /// Manifest shape (the shared Cybenetics manifest):
    ///   { "apps": { "pcue": { "version": "1.3.0.19", "url": "https://...", "sha256": "...",
    ///                         "notes": "..." } } }
    /// </summary>
    public sealed class AppUpdateService : IDisposable
    {
        /// <summary>
        /// Shared Cybenetics update manifest (public repo, so it is readable anonymously).
        /// Overridable via the Update_Manifest_Url setting.
        /// </summary>
        public const string DefaultManifestUrl =
            "https://raw.githubusercontent.com/crmaris/powenetics-updates/main/components.json";

        /// <summary>
        /// Optional Authenticode signer pin (certificate thumbprint, hex, case/space-insensitive).
        /// Empty (default) means integrity-only: HTTPS + sha256. When set, a verified download must
        /// ALSO carry an Authenticode signature from exactly this certificate, otherwise it is
        /// deleted and refused. Pin the release-signing cert here once one exists; until then leave
        /// empty — requiring a signature nobody produces would brick every update.
        /// </summary>
        public string ExpectedSignerThumbprint { get; set; } = "";

        /// <summary>This app's key inside the manifest's "apps" map.</summary>
        private const string ProductKey = "pcue";

        private readonly HttpClient _http;
        private bool _disposed;

        public AppUpdateService()
        {
            // .NET Framework honours the process-wide protocol list; make sure TLS 1.2 is on so
            // the request does not fail on machines still defaulting to TLS 1.0/1.1.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception ex) { Debug.WriteLine("pCUE: could not enable TLS 1.2: " + ex.Message); }

            _http = new() { Timeout = TimeSpan.FromSeconds(30) };   // target-typed new (C# 9)
            // GitHub's raw endpoint wants a User-Agent.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("pCUE-Updater");
        }

        /// <summary>Version of the running executable, e.g. "1.3.0.19" - the same value the title bar shows.</summary>
        public static string InstalledVersion
        {
            get
            {
                try
                {
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    string v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                    return string.IsNullOrWhiteSpace(v) ? "unknown" : v.Trim();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("pCUE: could not read installed version: " + ex.Message);
                    return "unknown";
                }
            }
        }

        // ---------------------------------------------------------------- check

        /// <summary>
        /// Fetches the manifest and decides whether an update is available. Never throws for the
        /// ordinary failure modes (offline, bad JSON) - those come back as State.Failed.
        /// </summary>
        public async Task<AppUpdateInfo> CheckAsync(string manifestUrl,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string installed = InstalledVersion;

            if (string.IsNullOrWhiteSpace(manifestUrl)) manifestUrl = DefaultManifestUrl;
            if (!IsHttps(manifestUrl))
                return AppUpdateInfo.Failure(installed, "Manifest URL must use HTTPS.");

            try
            {
                string json = await _http.GetStringAsync(manifestUrl).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return BuildFromManifest(json, installed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AppUpdateInfo.Failure(installed, "Could not read the update manifest: " + ex.Message);
            }
        }

        /// <summary>Turns the manifest into an update decision, rejecting incomplete entries.</summary>
        private static AppUpdateInfo BuildFromManifest(string json, string installed)
        {
            Dictionary<string, object> entry = null;
            try
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                object appsObj;
                if (root != null && root.TryGetValue("apps", out appsObj))
                {
                    var apps = appsObj as Dictionary<string, object>;
                    object mine;
                    if (apps != null && apps.TryGetValue(ProductKey, out mine))
                        entry = mine as Dictionary<string, object>;
                }
            }
            catch (Exception ex)
            {
                return AppUpdateInfo.Failure(installed, "Update manifest could not be parsed: " + ex.Message);
            }

            if (entry == null)
            {
                return AppUpdateInfo.Failure(installed,
                    "The update manifest has no entry for pCUE yet.");
            }

            string version = GetString(entry, "version");
            string url = GetString(entry, "url");
            string sha256 = GetString(entry, "sha256");
            string notes = GetString(entry, "notes");

            if (string.IsNullOrWhiteSpace(version))
                return AppUpdateInfo.Failure(installed, "The manifest entry for pCUE has no version.");

            if (!IsRemoteNewer(installed, version))
            {
                return new AppUpdateInfo
                {
                    State = UpdateCheckState.UpToDate,
                    InstalledVersion = installed,
                    AvailableVersion = version,
                    Message = "pCUE " + installed + " is current.",
                };
            }

            // A newer version is advertised, but we will not fetch it without an integrity anchor.
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sha256))
            {
                return AppUpdateInfo.Failure(installed,
                    "pCUE " + version + " is listed but its manifest entry is missing a url or " +
                    "sha256, so it cannot be verified. Not downloading.");
            }

            if (!IsHttps(url))
                return AppUpdateInfo.Failure(installed, "The download URL must use HTTPS.");

            string headline = "pCUE " + version + " is available (installed " + installed + ").";

            return new AppUpdateInfo
            {
                State = UpdateCheckState.UpdateAvailable,
                InstalledVersion = installed,
                AvailableVersion = version,
                DownloadUrl = url,
                Sha256 = sha256,
                Message = string.IsNullOrWhiteSpace(notes) ? headline : headline + " " + notes,
            };
        }

        // ---------------------------------------------------------------- download

        /// <summary>
        /// Downloads the installer and verifies its SHA-256. Returns the verified path on disk -
        /// it is NOT executed here. The caller must confirm with the user before launching it.
        /// Returns null (with a message) if anything fails; a mismatched file is deleted.
        /// Downloads are capped at 64 MB; cancelled/oversized/failed downloads are deleted so no
        /// partial file is left behind.
        /// </summary>
        public async Task<string> DownloadVerifiedInstallerAsync(AppUpdateInfo info,
            IProgress<string> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (info == null) throw new ArgumentNullException("info");
            if (info.State != UpdateCheckState.UpdateAvailable)
                throw new InvalidOperationException("No update is available to download.");
            if (!IsHttps(info.DownloadUrl))
                throw new InvalidOperationException("The download URL must use HTTPS.");
            if (string.IsNullOrWhiteSpace(info.Sha256))
                throw new InvalidOperationException("Refusing to download without an expected SHA-256.");

            const long MaxInstallerBytes = 64L * 1024 * 1024;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pCUE", "updates");
            Directory.CreateDirectory(dir);

            string fileName;
            try { fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath); }
            catch { fileName = null; }
            // Sanitize: URL filenames are attacker-influenced; keep only the leaf name.
            try { fileName = Path.GetFileName(fileName ?? ""); } catch { fileName = ""; }
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "pCUE_setup.exe";
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) fileName = "pCUE_setup.exe";
            string target = Path.Combine(dir, fileName);

            if (progress != null) progress.Report("Downloading " + fileName + "...");

            try
            {
                using (HttpResponseMessage response = await _http
                           .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead,
                                     cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength.HasValue &&
                        response.Content.Headers.ContentLength.Value > MaxInstallerBytes)
                        throw new InvalidOperationException("Update installer too large; refusing download.");
                    using (Stream src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await src.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxInstallerBytes)
                                throw new InvalidOperationException("Update installer too large; refusing download.");
                            await dst.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch
            {
                TryDelete(target);
                throw;
            }

            if (progress != null) progress.Report("Verifying checksum...");

            string actual = ComputeSha256(target);
            if (!actual.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                throw new InvalidOperationException(
                    "Checksum mismatch - the download was rejected and deleted. Expected " +
                    info.Sha256.Trim() + ", got " + actual + ".");
            }

            string pin = (ExpectedSignerThumbprint ?? "").Replace(" ", "").Trim();
            if (pin.Length > 0)
            {
                if (progress != null) progress.Report("Verifying signature...");
                string signer = GetAuthenticodeThumbprint(target);
                if (signer == null)
                {
                    TryDelete(target);
                    throw new InvalidOperationException(
                        "The download is unsigned but a signer pin is configured - rejected and deleted.");
                }
                if (!signer.Equals(pin, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(target);
                    throw new InvalidOperationException(
                        "Signer mismatch - the download was rejected and deleted. Expected " +
                        pin + ", got " + signer + ".");
                }
            }

            if (progress != null) progress.Report("Verified.");
            return target;
        }

        // ---------------------------------------------------------------- helpers

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            if (map != null && map.TryGetValue(key, out value) && value != null)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }

        private static bool IsHttps(string url)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out uri)
                && uri.Scheme == Uri.UriSchemeHttps;
        }

        /// <summary>
        /// True when <paramref name="remote"/> is a strictly newer version than
        /// <paramref name="installed"/>. Both are dotted numeric versions (e.g. "1.3.0.19").
        /// If either cannot be parsed we report "not newer" - never nag on a version we do not
        /// understand, and never offer a download we cannot justify.
        /// </summary>
        private static bool IsRemoteNewer(string installed, string remote)
        {
            Version a, b;
            if (!TryParseVersion(installed, out a)) return false;
            if (!TryParseVersion(remote, out b)) return false;
            return b > a;
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            // Version.TryParse needs at least major.minor; accept a bare "5" too.
            if (!text.Contains(".")) text += ".0";
            return Version.TryParse(text, out version);
        }

        private static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();          // using declarations (C# 8)
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        /// <summary>
        /// Thumbprint (no spaces, uppercase) of the Authenticode signing certificate, or null when
        /// the file is unsigned/unreadable. Signature CHAIN validity is deliberately not checked:
        /// the sha256 match already proves byte-integrity, so the pin only needs to prove origin.
        /// </summary>
        public static string GetAuthenticodeThumbprint(string path)
        {
            try
            {
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
                string thumb = cert.GetCertHashString();
                return string.IsNullOrWhiteSpace(thumb) ? null : thumb.Replace(" ", "");
            }
            catch
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Debug.WriteLine("pCUE: could not delete rejected download: " + ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _http.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: update HttpClient dispose failed: " + ex.Message); }
        }
    }
}
