using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Relay;

internal sealed record SshProfile(string Host, string Session)
{
    public static bool IsHost(string value) =>
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z");

    public static bool IsSession(string value) =>
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z");
}

internal static class Ssh
{
    public static string Executable =>
        Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");

    private const string Options = "-a -o ClearAllForwardings=yes -o BatchMode=yes " +
        "-o StrictHostKeyChecking=yes -o ConnectTimeout=8 -o ServerAliveInterval=10 -o ServerAliveCountMax=2";

    public static async Task<string?> ProbeAsync(SshProfile profile, CancellationToken token)
    {
        var result = await RunAsync(profile, $"tmux has-session -t '={profile.Session}'", null, token);
        return result.Code == 0 ? null : result.Failure;
    }

    internal sealed record Result(int Code, string Output, string Error)
    {
        public string Failure => !string.IsNullOrWhiteSpace(Error) ? Error.Trim() :
            !string.IsNullOrWhiteSpace(Output) ? Output.Trim() : $"OpenSSH завершился с кодом {Code}.";
    }

    public static async Task<Result> RunAsync(SshProfile profile, string command, string? input, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(18));
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-T");
        foreach (var option in Options.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            start.ArgumentList.Add(option);
        start.ArgumentList.Add(profile.Host);
        start.ArgumentList.Add(command);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить OpenSSH.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                if (input is not null)
                    await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // SSH may reject the connection before consuming the script.
            }
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
            return new Result(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new Result(-1, "", "Сервер не ответил за отведённое время.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
        }
    }

    public static PseudoTerminal Attach(SshProfile profile, int columns, int rows) =>
        PseudoTerminal.Start(Executable,
            $"-tt -e none {Options} {profile.Host} \"exec tmux attach-session -E -t '={profile.Session}'\"",
            columns, rows);
}
