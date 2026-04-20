using System.Diagnostics;
using System.Security.Principal;

namespace UrlAclManager.Shared;

public sealed class NetshResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
}

public static class UrlAclService
{
    public static bool IsRunningAsAdministrator()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
    }

    public static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (ContainsCommandBreakingChars(url)) return false;

        Uri? uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        bool isWildcard = url.StartsWith("http://+", StringComparison.OrdinalIgnoreCase) ||
                          url.StartsWith("https://+", StringComparison.OrdinalIgnoreCase);
        return !isWildcard || uri.Host == "+";
    }

    public static async Task<NetshResult> RunNetshAsync(string verb, string url, string? user = null)
    {
        if (!IsValidUrl(url))
        {
            return new NetshResult { Success = false, Output = "Invalid URL format." };
        }

        string safeUser = user ?? string.Empty;
        if (safeUser.Length > 0 && ContainsCommandBreakingChars(safeUser))
        {
            return new NetshResult { Success = false, Output = "Invalid user value." };
        }

        string args;
        switch (verb)
        {
            case "add":
                args = string.Format("http add urlacl url=\"{0}\" user=\"{1}\"", url, user ?? string.Empty);
                break;
            case "delete":
                args = string.Format("http delete urlacl url=\"{0}\"", url);
                break;
            default:
                throw new ArgumentException("Unknown verb: " + verb);
        }

        if (!IsRunningAsAdministrator())
        {
            return await RunElevatedNetshAsync(args).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    return new NetshResult { Success = false, Output = "Failed to start netsh process." };
                }

                string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await WaitForExitAsync(proc).ConfigureAwait(false);

                string combined = (stdout + "\n" + stderr).Trim();
                return new NetshResult { Success = proc.ExitCode == 0, Output = combined };
            }
        }
        catch (Exception ex)
        {
            return new NetshResult { Success = false, Output = ex.Message };
        }
    }

    public static async Task<IReadOnlyList<UrlAclEntry>> QuerySystemEntriesAsync()
    {
        var psi = new ProcessStartInfo("netsh", "http show urlacl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (var proc = Process.Start(psi))
        {
            if (proc == null)
            {
                return Array.Empty<UrlAclEntry>();
            }

            string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await WaitForExitAsync(proc).ConfigureAwait(false);
            return ParseUrlAclEntries(stdout);
        }
    }

    private static async Task<NetshResult> RunElevatedNetshAsync(string netshArgs)
    {
        string tempDir = Path.GetTempPath();
        string batFile = Path.Combine(tempDir, string.Format("urlacl_{0}.bat", Guid.NewGuid().ToString("N")));
        string resultFile = Path.Combine(tempDir, string.Format("urlacl_{0}.txt", Guid.NewGuid().ToString("N")));

        try
        {
            File.WriteAllText(
                batFile,
                string.Format("@echo off\r\nnetsh {0} > \"{1}\" 2>&1\r\necho EXIT_CODE=%ERRORLEVEL% >> \"{1}\"\r\n", netshArgs, resultFile));

            var psi = new ProcessStartInfo("cmd.exe", string.Format("/c \"{0}\"", batFile))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new NetshResult { Success = false, Output = "UAC_CANCELLED" };
            }

            if (proc == null) return new NetshResult { Success = false, Output = "Could not start process." };

            await WaitForExitAsync(proc).ConfigureAwait(false);

            string output = File.Exists(resultFile) ? File.ReadAllText(resultFile) : string.Empty;
            bool success = output.IndexOf("EXIT_CODE=0", StringComparison.Ordinal) >= 0;
            output = output.Replace(string.Format("EXIT_CODE={0}", proc.ExitCode), string.Empty).Trim();
            return new NetshResult { Success = success, Output = output };
        }
        finally
        {
            TryDelete(resultFile);
            TryDelete(batFile);
        }
    }

    private static List<UrlAclEntry> ParseUrlAclEntries(string netshOutput)
    {
        var entries = new List<UrlAclEntry>();
        string? currentUrl = null;

        foreach (var line in netshOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Reserved URL", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(':');
                if (parts.Length >= 2)
                {
                    var rawUrl = string.Join(":", parts.Skip(1).ToArray()).Trim();
                    currentUrl = string.IsNullOrWhiteSpace(rawUrl) ? null : rawUrl;
                }
            }
            else if (trimmed.StartsWith("User:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(currentUrl))
            {
                var userPart = trimmed.Substring("User:".Length).Trim();
                if (!string.IsNullOrEmpty(userPart))
                {
                    entries.Add(new UrlAclEntry
                    {
                        Url = currentUrl ?? string.Empty,
                        User = userPart,
                        RegisteredAt = DateTime.MinValue,
                        IsExternal = true
                    });
                }
            }
        }

        return entries;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static Task WaitForExitAsync(Process process)
    {
        return Task.Run(() => process.WaitForExit());
    }

    private static bool ContainsCommandBreakingChars(string value)
    {
        return value.IndexOf('"') >= 0 ||
               value.IndexOf('\r') >= 0 ||
               value.IndexOf('\n') >= 0;
    }
}
