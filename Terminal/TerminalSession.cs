using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CyberpunkTerminalWidget.Terminal
{
    public enum ShellType
    {
        PowerShell,
        CommandPrompt
    }

    /// <summary>
    /// Manages an interactive CLI process (PowerShell or CMD) with asynchronous I/O streaming,
    /// dynamic directory tracking, cross-drive navigation, command history, and non-blocking lifecycle guarantees.
    /// </summary>
    public class TerminalSession : IDisposable
    {
        private static readonly object SessionsLock = new object();
        private static readonly HashSet<TerminalSession> ActiveSessions = new HashSet<TerminalSession>();

        // Fallback prompt regex: matches prompts such as "PS C:\Users\macle> " or "C:\Users\macle>"
        private static readonly Regex PromptDirRegex = new Regex(@"^(?:PS\s+)?(?<dir>(?:[a-zA-Z]:|\\\\)[^>]*?)>\s*$", RegexOptions.Compiled);

        private readonly object _processLock = new object();
        private Process? _process;
        private StreamWriter? _inputWriter;
        private bool _isDisposed = false;
        private bool _isIntentionalRestart = false;

        public ShellType CurrentShell { get; private set; } = ShellType.PowerShell;
        public string WorkingDirectory { get; private set; }

        public event Action<string, bool>? OutputReceived; // (text, isError)
        public event Action<string>? DirectoryChanged;     // (newDirectory)
        public event Action<int>? ProcessExited;

        // Command history buffer
        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;

        private readonly ConcurrentQueue<string> _pendingEchoCommands = new ConcurrentQueue<string>();

        public TerminalSession(ShellType shellType = ShellType.PowerShell, string? initialDirectory = null)
        {
            CurrentShell = shellType;
            WorkingDirectory = string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : initialDirectory;

            lock (SessionsLock)
            {
                ActiveSessions.Add(this);
            }

            StartProcessInternal();
        }

        private static string FindPowerShellExecutable()
        {
            // 1. Check standard PowerShell 7+ install paths
            string pwsh7ProgramFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh7ProgramFiles)) return pwsh7ProgramFiles;

            string pwsh7Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerShell", "pwsh.exe");
            if (File.Exists(pwsh7Local)) return pwsh7Local;

            // 2. Check system PATH for pwsh.exe
            try
            {
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string candidate = Path.Combine(dir, "pwsh.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { }

            // 3. Fallback to standard Windows PowerShell
            return "powershell.exe";
        }

        private void StartProcessInternal()
        {
            lock (_processLock)
            {
                if (_isDisposed) return;

                string fileName;
                string arguments;

                if (CurrentShell == ShellType.PowerShell)
                {
                    fileName = FindPowerShellExecutable();
                    // Inject prompt definition directly into startup arguments as EncodedCommand
                    string psScript = "function prompt { \"##DIR##$((Get-Location).Path)##`n\" }";
                    string base64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
                    arguments = $"-NoProfile -NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand {base64}";
                }
                else
                {
                    fileName = "cmd.exe";
                    arguments = "/K prompt ##DIR##$P##$_";
                }

                var utf8NoBom = new UTF8Encoding(false);

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = utf8NoBom,
                    StandardErrorEncoding = utf8NoBom
                };

                try
                {
                    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            bool isInternalMarker = CheckForDirectoryPrompt(e.Data);
                            if (!isInternalMarker)
                            {
                                string dataTrimmed = e.Data.Trim();
                                if (_pendingEchoCommands.TryPeek(out var expectedCmd) &&
                                    (dataTrimmed.Equals(expectedCmd, StringComparison.OrdinalIgnoreCase) ||
                                     dataTrimmed.Equals("PS " + expectedCmd, StringComparison.OrdinalIgnoreCase)))
                                {
                                    _pendingEchoCommands.TryDequeue(out _);
                                    return; // Suppress echoed command line
                                }

                                OutputReceived?.Invoke(e.Data, false);
                            }
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            string errLine = e.Data.Trim();
                            // Filter out PowerShell CLIXML stream records
                            if (errLine.StartsWith("#< CLIXML") ||
                                errLine.StartsWith("<Objs") ||
                                errLine.StartsWith("<Obj") ||
                                errLine.Contains("schemas.microsoft.com/powershell"))
                            {
                                return;
                            }

                            OutputReceived?.Invoke(e.Data, true);
                        }
                    };

                    process.Exited += (s, e) =>
                    {
                        if (_isIntentionalRestart || _isDisposed) return;

                        int exitCode = 0;
                        try { exitCode = process.ExitCode; } catch { }
                        ProcessExited?.Invoke(exitCode);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    _process = process;
                    _inputWriter = process.StandardInput;
                }
                catch (Exception ex)
                {
                    OutputReceived?.Invoke($"[Terminal Error: Failed to launch {fileName}: {ex.Message}]", true);
                }
            }
        }

        private bool CheckForDirectoryPrompt(string line)
        {
            string trimmed = line.Trim();

            // Check for internal tracking marker "##DIR##...##"
            int markerStart = trimmed.IndexOf("##DIR##", StringComparison.Ordinal);
            if (markerStart >= 0)
            {
                int start = markerStart + "##DIR##".Length;
                int markerEnd = trimmed.IndexOf("##", start, StringComparison.Ordinal);
                if (markerEnd > start)
                {
                    string dir = trimmed.Substring(start, markerEnd - start).Trim();
                    if (!string.IsNullOrEmpty(dir))
                    {
                        WorkingDirectory = dir;
                        DirectoryChanged?.Invoke(dir);
                    }
                    return true; // Suppress printing internal tracking marker to console
                }
            }

            // Suppress echoing any prompt definition or markers
            if (trimmed.Contains("##DIR##") || trimmed.Contains("function prompt"))
            {
                return true;
            }

            // Fallback: match standard directory prompt e.g. "PS C:\Users\macle>" or "C:\Users\macle>"
            var match = PromptDirRegex.Match(trimmed);
            if (match.Success)
            {
                string dir = match.Groups["dir"].Value.Trim();
                if (!string.IsNullOrEmpty(dir))
                {
                    WorkingDirectory = dir;
                    DirectoryChanged?.Invoke(dir);
                }
            }

            return false;
        }

        /// <summary>
        /// Sends a command line to the shell's standard input stream, with automatic drive letter
        /// and directory navigation translation.
        /// </summary>
        public void SendCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                try
                {
                    _inputWriter?.WriteLine();
                    _inputWriter?.Flush();
                }
                catch { }
                return;
            }

            string trimmed = command.Trim();

            // Record to command history
            if (_history.Count == 0 || _history[^1] != command)
            {
                _history.Add(command);
            }
            _historyIndex = _history.Count;

            // Handle bare drive letter commands (e.g. "D:", "d:", "C:", "D:\")
            if (Regex.IsMatch(trimmed, @"^[a-zA-Z]:\\?$", RegexOptions.IgnoreCase))
            {
                string driveLetter = trimmed.Substring(0, 1).ToUpperInvariant();
                if (CurrentShell == ShellType.PowerShell)
                {
                    command = $"Set-Location '{driveLetter}:\\'";
                }
                else
                {
                    command = $"{driveLetter}:";
                }
            }
            // Handle "cd <drive>:..." in CMD mode (requires /d to actually switch drives in CMD)
            else if (CurrentShell == ShellType.CommandPrompt)
            {
                var cdMatch = Regex.Match(trimmed, @"^cd\s+(?!/d\s+)(?<path>.*)$", RegexOptions.IgnoreCase);
                if (cdMatch.Success)
                {
                    string path = cdMatch.Groups["path"].Value.Trim();
                    if (Regex.IsMatch(path, @"^[a-zA-Z]:", RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(path, @"^""[a-zA-Z]:", RegexOptions.IgnoreCase))
                    {
                        command = $"cd /d {path}";
                    }
                }
            }

            // Maintain bounded pending echo queue to avoid memory leak
            while (_pendingEchoCommands.Count > 50)
            {
                _pendingEchoCommands.TryDequeue(out _);
            }
            _pendingEchoCommands.Enqueue(trimmed);

            // Forward to process standard input
            try
            {
                _inputWriter?.WriteLine(command);
                _inputWriter?.Flush();
            }
            catch (Exception ex)
            {
                OutputReceived?.Invoke($"[Error writing to terminal process: {ex.Message}]", true);
            }
        }

        /// <summary>
        /// Sends a break signal or restarts the active shell asynchronously without locking up the UI.
        /// </summary>
        public void SendBreak()
        {
            RestartSession();
            OutputReceived?.Invoke("[Process interrupted / shell re-initialized]", true);
        }

        /// <summary>
        /// Restarts the active shell session cleanly on a background worker thread.
        /// </summary>
        public void RestartSession()
        {
            Task.Run(() =>
            {
                lock (_processLock)
                {
                    _isIntentionalRestart = true;
                    try
                    {
                        KillCurrentProcessInternal();
                        StartProcessInternal();
                    }
                    finally
                    {
                        _isIntentionalRestart = false;
                    }
                }
            });
        }

        /// <summary>
        /// Switches between Windows PowerShell and Command Prompt without blocking.
        /// </summary>
        public void SwitchShell(ShellType newShell)
        {
            if (CurrentShell == newShell) return;
            CurrentShell = newShell;
            RestartSession();
        }

        public string? GetPreviousHistory(string currentInput)
        {
            if (_history.Count == 0) return null;
            if (_historyIndex > 0)
            {
                _historyIndex--;
                return _history[_historyIndex];
            }
            else if (_historyIndex == 0)
            {
                return _history[0];
            }
            return null;
        }

        public string? GetNextHistory()
        {
            if (_history.Count == 0) return null;
            if (_historyIndex < _history.Count - 1)
            {
                _historyIndex++;
                return _history[_historyIndex];
            }
            else
            {
                _historyIndex = _history.Count;
                return string.Empty;
            }
        }

        private void KillCurrentProcessInternal()
        {
            lock (_processLock)
            {
                if (_process == null) return;

                try
                {
                    _process.EnableRaisingEvents = false;
                }
                catch { }

                try
                {
                    _inputWriter?.Close();
                }
                catch { }

                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(true); // Kill process tree
                        _process.WaitForExit(1000);
                    }
                }
                catch { }

                try
                {
                    _process.CancelOutputRead();
                    _process.CancelErrorRead();
                }
                catch { }

                try
                {
                    _process.Dispose();
                }
                catch { }

                _process = null;
                _inputWriter = null;
            }
        }

        public static void KillAllActiveSessions()
        {
            lock (SessionsLock)
            {
                foreach (var session in ActiveSessions)
                {
                    session.KillCurrentProcessInternal();
                }
                ActiveSessions.Clear();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            lock (SessionsLock)
            {
                ActiveSessions.Remove(this);
            }

            KillCurrentProcessInternal();

            GC.SuppressFinalize(this);
        }
    }
}

