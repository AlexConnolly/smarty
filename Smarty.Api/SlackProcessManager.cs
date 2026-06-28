using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Smarty.Api;

public class SlackProcessManager : IHostedService, IDisposable
{
    private readonly SettingsDatabase _db;
    private readonly IHostEnvironment _env;
    private Process? _slackProcess;
    private Timer? _timer;
    private readonly object _lock = new();

    public SlackProcessManager(SettingsDatabase db, IHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Check setting state every 4 seconds
        _timer = new Timer(CheckProcess, null, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        return Task.CompletedTask;
    }

    private void CheckProcess(object? state)
    {
        lock (_lock)
        {
            bool isEnabled = _db.GetSetting("slack.enabled", "false") == "true";
            string botToken = _db.GetSetting("slack.botToken", "")!;
            string appToken = _db.GetSetting("slack.appToken", "")!;

            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(appToken))
            {
                isEnabled = false;
            }

            if (isEnabled)
            {
                if (_slackProcess == null || _slackProcess.HasExited)
                {
                    StartSlackProcess(botToken, appToken);
                }
            }
            else
            {
                if (_slackProcess != null && !_slackProcess.HasExited)
                {
                    StopSlackProcess();
                }
            }
        }
    }

    private void StartSlackProcess(string botToken, string appToken)
    {
        try
        {
            string companyName = _db.GetSetting("slack.companyName", "Our Company")!;
            string companyContext = _db.GetSetting("slack.companyContext", "")!;
            string dataDir = _db.GetSetting("slack.dataDir", "data-slack")!;

            string projectDir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "Smarty.Slack"));

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run",
                WorkingDirectory = projectDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.EnvironmentVariables["SLACK_BOT_TOKEN"] = botToken;
            psi.EnvironmentVariables["SLACK_APP_TOKEN"] = appToken;
            psi.EnvironmentVariables["SMARTY_COMPANY_NAME"] = companyName;
            psi.EnvironmentVariables["SMARTY_COMPANY_CONTEXT"] = companyContext;
            psi.EnvironmentVariables["SMARTY_DATA_DIR"] = dataDir;

            _slackProcess = Process.Start(psi);
        }
        catch
        {
            // Fail silently, retry on next tick
        }
    }

    private void StopSlackProcess()
    {
        try
        {
            if (_slackProcess != null && !_slackProcess.HasExited)
            {
                _slackProcess.Kill(true);
            }
        }
        catch
        {
        }
        finally
        {
            _slackProcess = null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        lock (_lock)
        {
            StopSlackProcess();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _slackProcess?.Dispose();
    }
}
