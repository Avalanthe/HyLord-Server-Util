using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HyLordServerUtil
{
    public class ServerProcess
    {
        private Process process;
        private bool intentionalStop;

        public string ServerDirectory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

        public int NetworkPort { get; set; } = 5520;

        public Process Process => process;

        public event Action<string> OutputReceived;
        public event Action ServerStarted;
        public event Action ServerStopped;
        public event Action ServerCrashed;

        public event Action<PlayerInfo> PlayerJoined;
        public event Action<PlayerInfo> PlayerLeft;
        public event Action<PlayerInfo>? PlayerRejected;
        public bool IsRunning
        {
            get
            {
                return Process != null && !Process.HasExited;
            }
        }
        public void Start()
        {
            if (process != null && !process.HasExited)
                return;

            intentionalStop = false;

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = $"-jar HytaleServer.jar --assets Assets.zip --bind 0.0.0.0:{NetworkPort}",
                    WorkingDirectory = ServerDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += OnExited;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            ServerStarted?.Invoke();
        }

        public void Stop()
        {
            if (process == null || process.HasExited)
                return;

            intentionalStop = true;

            try
            {
                SendCommand("stop");

                if (!process.WaitForExit(8000))
                    process.Kill();
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
        }

        public async void Restart()
        {
            intentionalStop = true;

            if (process != null && !process.HasExited)
            {
                try
                {
                    SendCommand("stop");
                    await Task.Run(() => process.WaitForExit(8000));
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    try { process.Kill(); } catch { }
                }
            }

            intentionalStop = false;
            await Task.Delay(750);
            Start();
        }

        public void SendCommand(string command)
        {
            try
            {
                if (process == null || process.HasExited)
                    return;

                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
            }
            catch
            {
                // fuckit
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            OutputReceived?.Invoke(e.Data);
            ParsePlayerEvents(e.Data);
        }

        private void OnExited(object sender, EventArgs e)
        {
            if (intentionalStop)
                ServerStopped?.Invoke();
            else
                ServerCrashed?.Invoke();
        }

        private readonly Regex joinRegex =
            new(@"Mutual authentication complete for\s+(?<name>[^\s]+)\s+\((?<hash>[^)]+)\)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Regex leaveRegex =
            new(@"Checking objectives for disconnecting player\s+(?<name>[^\s]+)\s+\((?<hash>[^)]+)\)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        private readonly Regex bannedClosedRegex =
            new(@"\{\s*Setup\(null\s*\(null,\s*streamId=\d+\)\),\s*(?<name>[^,]+),\s*(?<hash>[^,]+),\s*SECURE\s*\}\s*was\s+closed\.",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void ParsePlayerEvents(string line)
        {
            var closed = bannedClosedRegex.Match(line);
            if (closed.Success)
            {
                PlayerLeft?.Invoke(new PlayerInfo
                {
                    Name = closed.Groups["name"].Value.Trim(),
                    Hash = closed.Groups["hash"].Value.Trim()
                });
                return;
            }

            var leave = leaveRegex.Match(line);
            if (leave.Success)
            {
                PlayerLeft?.Invoke(new PlayerInfo
                {
                    Name = leave.Groups["name"].Value.Trim(),
                    Hash = leave.Groups["hash"].Value.Trim()
                });
                return;
            }

            var join = joinRegex.Match(line);
            if (join.Success)
            {
                PlayerJoined?.Invoke(new PlayerInfo
                {
                    Name = join.Groups["name"].Value.Trim(),
                    Hash = join.Groups["hash"].Value.Trim(),
                    JoinedAt = DateTime.Now
                });
                return;
            }
        }




    }
}
