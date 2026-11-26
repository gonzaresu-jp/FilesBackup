using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilesBackup.Services
{
    /// <summary>新しいバージョンの情報（GitHub Releases の最新リリース）</summary>
    public sealed record UpdateInfo(Version Version, string Tag, string Name, string Notes, string ZipUrl, string? ShaUrl, string PageUrl, long Size,
                                    string? Sha256 = null);

    /// <summary>
    /// GitHub Releases から新しいバージョンを確認し、ダウンロード・検証して入れ替える。
    ///  1. 最新リリースのタグ（例: v2.1.0）と今のバージョンを比べる
    ///  2. FilesBackup*.zip をダウンロードし、同名の .sha256 があればハッシュを照合する
    ///  3. アプリを終了し、別プロセス（PowerShell）でファイルを入れ替えて起動し直す
    ///     Program Files など書き込みに管理者権限が必要な場所なら、UAC の確認が出る
    /// </summary>
    public static class UpdateService
    {
        public const string Owner = "gonzaresu-jp";
        public const string Repo = "FilesBackup";
        public static readonly string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FilesBackup-Updater", CurrentVersion.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return http;
        });

        public static Version CurrentVersion => Normalize(Assembly.GetEntryAssembly()?.GetName().Version
                                                          ?? typeof(UpdateService).Assembly.GetName().Version
                                                          ?? new Version(0, 0, 0));

        private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>"v2.1.0" や "2.1" をバージョンとして読む（読めなければ null）</summary>
        public static Version? ParseTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(t, out var v) ? Normalize(v) : null;
        }

        /// <summary>GitHub API の応答（最新リリース）を読む。対象のファイルが無い・下書きなどは null</summary>
        public static UpdateInfo? ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var version = ParseTag(tag);
            if (version == null) return null;

            string? zipUrl = null, shaUrl = null, zipName = null, digest = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (zipUrl == null && name.StartsWith("FilesBackup", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = a.GetProperty("browser_download_url").GetString();
                        zipName = name;
                        size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                        // GitHub が記録しているファイルのハッシュ（"sha256:..."）
                        if (a.TryGetProperty("digest", out var dg) && dg.GetString() is string d &&
                            d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            digest = ParseSha256(d[7..]);
                    }
                }

                if (zipName != null)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.GetProperty("name").GetString() ?? "";
                        if (name.Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                            shaUrl = a.GetProperty("browser_download_url").GetString();
                    }
                }
            }

            if (zipUrl == null) return null;

            return new UpdateInfo(
                version,
                tag,
                root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                zipUrl,
                shaUrl,
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                size,
                digest);
        }

        /// <summary>新しいバージョンがあれば返す（無ければ null）</summary>
        public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            using var response = await Http.Value.GetAsync(LatestReleaseApi, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;   // リリースがまだ無い
            response.EnsureSuccessStatusCode();

            var info = ParseRelease(await response.Content.ReadAsStringAsync(ct));
            return info != null && info.Version > CurrentVersion ? info : null;
        }

        /// <summary>取得先は GitHub の HTTPS のみ許可する</summary>
        public static bool IsTrustedUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            u.Scheme == Uri.UriSchemeHttps &&
            (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
             u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

        public static string? ParseSha256(string text)
        {
            var m = Regex.Match(text, @"\b[0-9a-fA-F]{64}\b");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        /// <summary>更新ファイルをダウンロードして検証する。保存先の zip のパスを返す</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
        {
            if (!IsTrustedUrl(info.ZipUrl))
                throw new InvalidDataException("更新ファイルの取得先が GitHub ではありません。");

            string dir = Path.Combine(Path.GetTempPath(), "FilesBackupUpdate");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, $"FilesBackup_{info.Version}.zip");

            using (var response = await Http.Value.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? info.Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            // ハッシュの照合（.sha256 ファイル、無ければ GitHub が記録しているハッシュ）
            string? expected = info.Sha256;
            if (info.ShaUrl != null)
            {
                if (!IsTrustedUrl(info.ShaUrl))
                    throw new InvalidDataException("ハッシュファイルの取得先が GitHub ではありません。");
                expected = ParseSha256(await Http.Value.GetStringAsync(info.ShaUrl, ct))
                           ?? throw new InvalidDataException("ハッシュファイルを読み取れませんでした。");
            }

            if (expected != null)
            {
                string actual;
                await using (var fs = File.OpenRead(zip))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();

                if (actual != expected)
                    throw new InvalidDataException("ダウンロードしたファイルが壊れています（ハッシュが一致しません）。");
            }

            using (var archive = ZipFile.OpenRead(zip))
            {
                if (!archive.Entries.Any(e => e.Name.Equals("FilesBackup.exe", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("更新ファイルに FilesBackup.exe が含まれていません。");
            }

            return zip;
        }

        /// <summary>インストール先に書き込めないなら、入れ替えに管理者権限が必要</summary>
        public static bool NeedsElevation(string installDir)
        {
            try
            {
                string probe = Path.Combine(installDir, $".update_write_test_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// ファイルを入れ替えるスクリプトを起動する。呼び出し側はこの後すぐアプリを終了すること。
        /// UAC で「いいえ」が選ばれた場合は Win32Exception（NativeErrorCode 1223）になる。
        /// </summary>
        public static void LaunchInstaller(string zip, string installDir)
        {
            installDir = installDir.TrimEnd('\\', '/');
            string dir = Path.GetDirectoryName(zip)!;
            string script = Path.Combine(dir, "install_update.ps1");
            File.WriteAllText(script, InstallerScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            string backup = Path.Combine(AppPaths.InstallBackupDir, $"v{CurrentVersion}_{DateTime.Now:yyyyMMdd_HHmmss}");
            int pid = Environment.ProcessId;

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                            $"-Zip \"{zip}\" -InstallDir \"{installDir}\" -ProcessId {pid} " +
                            $"-BackupDir \"{backup}\" -ResultPath \"{AppPaths.UpdateResultPath}\"",
            };
            if (NeedsElevation(installDir))
                psi.Verb = "runas";

            Logger.Info($"アップデートを開始します: {CurrentVersion} → {Path.GetFileName(zip)}（{installDir}）");
            Process.Start(psi);
        }

        /// <summary>更新後の起動時に、結果を読んで消す（"OK 2.1.0" または "ERROR 理由"）</summary>
        public static string? TakeResult()
        {
            try
            {
                if (!File.Exists(AppPaths.UpdateResultPath)) return null;
                string text = File.ReadAllText(AppPaths.UpdateResultPath).Trim();
                File.Delete(AppPaths.UpdateResultPath);
                return text;
            }
            catch
            {
                return null;
            }
        }

        // 入れ替え用スクリプト（Windows PowerShell 5.1 でも動くよう、文字列は英語のみ）
        public const string InstallerScript = """
            param(
                [string]$Zip,
                [string]$InstallDir,
                [int]$ProcessId,
                [string]$BackupDir,
                [string]$ResultPath,
                [string]$ProcessName = 'FilesBackup',
                [switch]$NoRestart
            )

            $ErrorActionPreference = 'Stop'

            function Write-Result([string]$text) {
                $dir = Split-Path -Parent $ResultPath
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                Set-Content -Path $ResultPath -Value $text -Encoding UTF8
            }

            try {
                # Wait for the app to exit, then for any other instance (e.g. a scheduled backup) to finish.
                try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch { }
                $deadline = (Get-Date).AddMinutes(30)
                while (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) {
                    if ((Get-Date) -gt $deadline) { throw 'FilesBackup is still running (a backup may be in progress).' }
                    Start-Sleep -Seconds 5
                }

                $staging = Join-Path (Split-Path -Parent $Zip) 'staging'
                if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
                # Expand-Archive fails on some Windows PowerShell 5.1 setups, so use .NET directly.
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $staging)

                $exe = Get-ChildItem -Path $staging -Recurse -Filter 'FilesBackup.exe' | Select-Object -First 1
                if (-not $exe) { throw 'FilesBackup.exe was not found in the update package.' }
                $source = $exe.DirectoryName

                # Keep the current version so it can be restored.
                New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
                Get-ChildItem -Path $InstallDir | Where-Object { $_.Name -ne 'Config' } |
                    Copy-Item -Destination $BackupDir -Recurse -Force

                try {
                    Get-ChildItem -Path $source | Where-Object { $_.Name -ne 'Config' } |
                        Copy-Item -Destination $InstallDir -Recurse -Force
                }
                catch {
                    Get-ChildItem -Path $BackupDir | Copy-Item -Destination $InstallDir -Recurse -Force
                    throw ('Copy failed and the previous version was restored: ' + $_.Exception.Message)
                }

                $version = (Get-Item (Join-Path $InstallDir 'FilesBackup.exe')).VersionInfo.FileVersion
                Write-Result ('OK ' + $version)
            }
            catch {
                Write-Result ('ERROR ' + $_.Exception.Message)
            }
            finally {
                # Start the app again as the signed-in user (not elevated).
                if (-not $NoRestart) {
                    $app = Join-Path $InstallDir 'FilesBackup.exe'
                    Start-Process -FilePath explorer.exe -ArgumentList ('"' + $app + '"')
                }
            }
            """;
    }
}
