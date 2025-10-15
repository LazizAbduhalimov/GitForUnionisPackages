using System;
using System.Diagnostics;
using System.Text;

namespace EasyGit
{
    public static class GitCommand
    {
        public static bool Run(string arguments, string workingDirectory, out string allOutput, int timeoutMs = 10 * 60 * 1000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var sb = new StringBuilder();
            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    allOutput = "[Git] Timeout";
                    return false;
                }

                allOutput = sb.ToString();
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                allOutput = "[Git] Exception: " + ex.Message;
                return false;
            }
        }
    }
}
