using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

public sealed class DataScienceCapability : ICapability
{
    private string _pythonCmd = "python";

    public string Id => "datascience";
    public string DisplayName => "Data Science (Python code execution & report generation)";
    public IReadOnlyList<string> RequiredConfig => Array.Empty<string>();

    public string? PromptHint =>
        "Use run_python to execute Python code, read/process CSVs/data, render documents (PDF via reportlab) and " +
        "charts (PNG). ALL of this conversation's files are already present in the working directory each run — " +
        "open them by bare name in your code (no need to read_file them first, or to list them in 'files'). Files " +
        "you generate are saved to the conversation automatically and handed to the user when you finish.";

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        return new[]
        {
            new AgentTool(
                "run_python",
                "Executes Python code. You can list files from this conversation that should be copied into the script's working directory so the script can read them. Any new or modified files the script generates will be saved to this conversation's files area.",
                new[]
                {
                    ToolParameter.String("code", "The complete Python code to execute. Standard output and standard error will be captured.", required: true),
                    ToolParameter.String("files", "A JSON array of string filenames from the conversation's files area to copy into the execution directory, e.g. '[\"data.csv\"]'.", required: false)
                },
                (args, ct) => RunPythonAsync(args, task, ct))
        };
    }

    public void ValidateSystemPrerequisites()
    {
        Console.WriteLine("[datascience] Verifying python executable is in PATH...");
        try
        {
            _pythonCmd = ResolvePythonCommand();
            Console.WriteLine($"[datascience] Using Python command: '{_pythonCmd}'");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Python executable was not found and could not be installed automatically. " +
                "Please make sure Python is installed and added to your system PATH. " +
                $"Error: {ex.Message}");
        }

        // Verify version
        try
        {
            string version = RunSimpleCommand(_pythonCmd, "--version").Trim();
            Console.WriteLine($"[datascience] Found Python version: {version}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to retrieve Python version: {ex.Message}");
        }

        // Verify imports
        Console.WriteLine("[datascience] Verifying required libraries (pandas, numpy, scipy, matplotlib, seaborn, reportlab, openpyxl, pillow)...");
        bool needsInstall = false;
        try
        {
            string output = RunSimpleCommand(_pythonCmd, "-c \"import pandas; import numpy; import scipy; import matplotlib; import seaborn; import reportlab; import openpyxl; from PIL import Image; print('ok')\"").Trim();
            if (output != "ok") needsInstall = true;
        }
        catch
        {
            needsInstall = true;
        }

        if (needsInstall)
        {
            Console.WriteLine("[datascience] Some required libraries are missing. Installing via pip...");
            try
            {
                RunCommandAndShowOutput(_pythonCmd, "-m pip install pandas numpy scipy matplotlib seaborn reportlab openpyxl pillow");
                Console.WriteLine("[datascience] Libraries successfully installed.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Required Python packages (pandas, numpy, scipy, matplotlib, seaborn, reportlab, openpyxl, pillow) are missing and automatic pip installation failed. " +
                    "Please install them manually: pip install pandas numpy scipy matplotlib seaborn reportlab openpyxl pillow. " +
                    $"Error: {ex.Message}");
            }

            // Verify again
            try
            {
                string output = RunSimpleCommand(_pythonCmd, "-c \"import pandas; import numpy; import scipy; import matplotlib; import seaborn; import reportlab; import openpyxl; from PIL import Image; print('ok')\"").Trim();
                if (output != "ok")
                {
                    throw new Exception("Prerequisites validation failed after installation.");
                }
                Console.WriteLine("[datascience] Required libraries (pandas, numpy, scipy, matplotlib, seaborn, reportlab, openpyxl) are successfully verified.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Prerequisites validation failed after pip installation: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[datascience] Required libraries (pandas, numpy, scipy, matplotlib, seaborn, reportlab, openpyxl) are already installed.");
        }
    }

    private string ResolvePythonCommand()
    {
        // 1. Try 'python' and 'python3' in PATH
        foreach (var cmd in new[] { "python", "python3" })
        {
            if (CheckPythonExecutable(cmd))
            {
                return cmd;
            }
        }

        // 2. Check standard Windows directories
        var standardPaths = GetStandardPythonPaths();
        foreach (var path in standardPaths)
        {
            if (CheckPythonExecutable(path))
            {
                return path;
            }
        }

        // 3. Try to install via winget
        Console.WriteLine("[datascience] Python not found in PATH or standard folders.");
        Console.WriteLine("[datascience] Attempting to install Python via winget (this may take a few minutes)...");
        try
        {
            RunCommandAndShowOutput("winget", "install Python.Python.3 --silent --accept-source-agreements --accept-package-agreements");
            Console.WriteLine("[datascience] winget installation completed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[datascience] winget installation failed: {ex.Message}");
        }

        // 4. Re-check standard Windows directories after install
        standardPaths = GetStandardPythonPaths();
        foreach (var path in standardPaths)
        {
            if (CheckPythonExecutable(path))
            {
                return path;
            }
        }

        throw new InvalidOperationException("Python was not found and automatic installation failed.");
    }

    private static bool CheckPythonExecutable(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(TimeSpan.FromSeconds(5));
                if (process.HasExited && process.ExitCode == 0)
                {
                    return true;
                }
            }
        }
        catch {}
        return false;
    }

    private static List<string> GetStandardPythonPaths()
    {
        var paths = new List<string>();
        
        // LocalAppData
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var programsDir = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(programsDir))
            {
                foreach (var dir in Directory.GetDirectories(programsDir))
                {
                    var exePath = Path.Combine(dir, "python.exe");
                    if (File.Exists(exePath)) paths.Add(exePath);
                }
            }
        }

        // ProgramFiles
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            var pythonDir = Path.Combine(programFiles, "Python");
            if (Directory.Exists(pythonDir))
            {
                foreach (var dir in Directory.GetDirectories(pythonDir))
                {
                    var exePath = Path.Combine(dir, "python.exe");
                    if (File.Exists(exePath)) paths.Add(exePath);
                }
            }
        }

        // ProgramFilesX86
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            var pythonDir = Path.Combine(programFilesX86, "Python");
            if (Directory.Exists(pythonDir))
            {
                foreach (var dir in Directory.GetDirectories(pythonDir))
                {
                    var exePath = Path.Combine(dir, "python.exe");
                    if (File.Exists(exePath)) paths.Add(exePath);
                }
            }
        }

        // Common custom root directories (e.g. C:\Python39)
        try
        {
            var drives = Directory.GetLogicalDrives();
            foreach (var drive in drives)
            {
                if (Directory.Exists(drive))
                {
                    foreach (var dir in Directory.GetDirectories(drive))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                        {
                            var exePath = Path.Combine(dir, "python.exe");
                            if (File.Exists(exePath)) paths.Add(exePath);
                        }
                    }
                }
            }
        }
        catch {}

        return paths;
    }

    private static void RunCommandAndShowOutput(string cmd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) throw new Exception($"Could not start process {cmd}");

        // Read output dynamically to show tracking logs
        while (!process.StandardOutput.EndOfStream)
        {
            string? line = process.StandardOutput.ReadLine();
            if (line != null)
            {
                Console.WriteLine($"[{cmd}] {line}");
            }
        }
        while (!process.StandardError.EndOfStream)
        {
            string? line = process.StandardError.ReadLine();
            if (line != null)
            {
                Console.WriteLine($"[{cmd} error] {line}");
            }
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Exception($"Process exited with code {process.ExitCode}");
        }
    }

    private static string RunSimpleCommand(string cmd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) throw new Exception($"Could not start process {cmd}");
        process.WaitForExit(TimeSpan.FromSeconds(15));
        if (!process.HasExited)
        {
            process.Kill();
            throw new Exception("Process execution timed out.");
        }
        if (process.ExitCode != 0)
        {
            string err = process.StandardError.ReadToEnd();
            string outStr = process.StandardOutput.ReadToEnd();
            throw new Exception($"Process exited with code {process.ExitCode}. Output: {outStr}. Error: {err}");
        }
        return process.StandardOutput.ReadToEnd();
    }

    private async Task<ToolOutput> RunPythonAsync(ToolCallArguments args, TaskInfo task, CancellationToken ct)
    {
        string code = args.GetString("code");

        // WeasyPrint and pdfkit can't render in this environment (missing native GTK/wkhtmltopdf libs) — every
        // attempt fails and the worker rabbit-holes into base64-inlining HTML. Reject the import up front with a
        // clear steer to reportlab, deterministically, rather than letting it waste a run discovering that again.
        if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\b(import\s+weasyprint|from\s+weasyprint|import\s+pdfkit|from\s+pdfkit)\b"))
            return ToolOutput.Error(
                "WeasyPrint and pdfkit do NOT work in this environment (no native render libs) — they will fail. " +
                "Build the PDF with reportlab instead (it is installed and works). Do not use an HTML→PDF renderer.");

        // We need a temp directory for execution inside the task workspace.
        string baseDir = task.WorkspaceDir ?? Path.GetTempPath();
        string execDir = Path.Combine(baseDir, $"py_{Guid.NewGuid():N}");
        string? threadFilesDir = string.IsNullOrEmpty(task.WorkspaceDir)
            ? null
            : Path.Combine(Path.GetDirectoryName(task.WorkspaceDir)!, "files");

        try
        {
            Directory.CreateDirectory(execDir);

            // Stage the WHOLE conversation file area into the working dir every run. run_python is otherwise
            // stateless per call (fresh temp dir), so files a previous run downloaded/produced would vanish —
            // which drove the worker to cram everything into one giant script and base64-inline fonts. With all
            // files present each run, code can just open them by bare name; no read_file paging, no re-staging.
            if (!string.IsNullOrEmpty(threadFilesDir) && Directory.Exists(threadFilesDir))
            {
                foreach (var src in Directory.GetFiles(threadFilesDir))
                    File.Copy(src, Path.Combine(execDir, Path.GetFileName(src)), overwrite: true);
            }

            // Write the python code to script.py
            string scriptPath = Path.Combine(execDir, "script.py");
            await File.WriteAllTextAsync(scriptPath, code, ct);

            // Record initial files and their write times to detect changes/new files
            var initialFiles = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(execDir))
            {
                initialFiles[Path.GetFileName(f)] = File.GetLastWriteTimeUtc(f);
            }

            // Start Python process
            var psi = new ProcessStartInfo
            {
                FileName = _pythonCmd,
                Arguments = "script.py",
                WorkingDirectory = execDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // Wait for exit or timeout (e.g. 60 seconds)
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60), ct);
            var completedTask = await Task.WhenAny(process.WaitForExitAsync(ct), timeoutTask);

            if (completedTask == timeoutTask)
            {
                try { process.Kill(); } catch {}
                return ToolOutput.Error("Python execution timed out (limit: 60s).");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            // Find new or modified files in execDir and copy them back to threadFilesDir
            var generatedFiles = new List<string>();
            if (!string.IsNullOrEmpty(threadFilesDir))
            {
                Directory.CreateDirectory(threadFilesDir);
                foreach (var file in Directory.GetFiles(execDir))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("script.py", StringComparison.OrdinalIgnoreCase)) continue;

                    bool isNew = !initialFiles.TryGetValue(name, out var initialTime);
                    bool isModified = !isNew && File.GetLastWriteTimeUtc(file) > initialTime;

                    if (isNew || isModified)
                    {
                        var dest = Path.Combine(threadFilesDir, name);
                        File.Copy(file, dest, overwrite: true);
                        generatedFiles.Add(name);
                    }
                }

                // Harvest any font this run produced (typically downloaded) into the shared, cross-thread font
                // cache, so the next brand task can reference it instead of re-downloading. Best-effort: a cache
                // miss just means a download next time — it must never fail the run. Root is two levels up from
                // the thread's files area (<root>/<session>/files → <root>).
                try
                {
                    var root = Path.GetDirectoryName(Path.GetDirectoryName(threadFilesDir));
                    if (Orchestrator.FontCacheDirFor(root) is { } cacheDir)
                        foreach (var name in generatedFiles)
                        {
                            var ext = Path.GetExtension(name).ToLowerInvariant();
                            if (ext is not (".ttf" or ".otf" or ".woff" or ".woff2")) continue;
                            var cached = Path.Combine(cacheDir, name);
                            if (File.Exists(cached)) continue; // already cached — don't churn
                            Directory.CreateDirectory(cacheDir);
                            File.Copy(Path.Combine(threadFilesDir, name), cached, overwrite: false);
                        }
                }
                catch { /* caching is an optimisation, never fatal */ }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Execution finished with exit code {process.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                sb.AppendLine("--- stdout ---");
                sb.AppendLine(stdout);
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                sb.AppendLine("--- stderr ---");
                sb.AppendLine(stderr);
            }
            if (generatedFiles.Count > 0)
            {
                sb.AppendLine("--- generated files (copied to conversation) ---");
                foreach (var f in generatedFiles)
                {
                    sb.AppendLine($"- {f}");
                }
            }

            if (process.ExitCode != 0)
            {
                return ToolOutput.Error(sb.ToString().TrimEnd());
            }
            return ToolOutput.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Failed to execute Python script: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(execDir))
                {
                    Directory.Delete(execDir, recursive: true);
                }
            }
            catch {}
        }
    }
}
