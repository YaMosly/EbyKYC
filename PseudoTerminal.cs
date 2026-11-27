using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Relay;

internal sealed class PseudoTerminal
{
    private readonly object gate = new();
    private readonly Process child;
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly Channel<string> pendingInput = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private IntPtr console;

    private PseudoTerminal(IntPtr console, Process child, SafeFileHandle input, SafeFileHandle output)
    {
        this.console = console;
        this.child = child;
        this.input = new FileStream(input, FileAccess.Write, 8192, isAsync: false);
        this.output = new FileStream(output, FileAccess.Read, 8192, isAsync: false);
    }

    public static PseudoTerminal Start(string executable, string args, int columns, int rows)
    {
        if (!CreatePipe(out var terminalInput, out var appInput, IntPtr.Zero, 0))
            throw new Win32Exception();
        using (terminalInput)
        {
            if (!CreatePipe(out var appOutput, out var terminalOutput, IntPtr.Zero, 0))
            {
                appInput.Dispose();
                throw new Win32Exception();
            }
            using (terminalOutput)
            {
                var console = IntPtr.Zero;
                try
                {
                    Marshal.ThrowExceptionForHR(CreatePseudoConsole(Size(columns, rows), terminalInput, terminalOutput, 0, out console));
                    var child = StartChild(console, executable, args);
                    return new PseudoTerminal(console, child, appInput, appOutput);
                }
                catch
                {
                    appInput.Dispose();
                    appOutput.Dispose();
                    if (console != IntPtr.Zero)
                        ClosePseudoConsole(console);
                    throw;
                }
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (gate)
        {
            if (console != IntPtr.Zero)
                ResizePseudoConsole(console, Size(columns, rows));
        }
    }

    public bool QueueInput(string text) => pendingInput.Writer.TryWrite(text);

    public async Task<int> ReadAsync(Func<byte[], int, Task> receive, CancellationToken token)
    {
        using var cancellation = token.Register(() =>
        {
            try { child.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        var exit = Task.Run(async () =>
        {
            try
            {
                await child.WaitForExitAsync();
                return child.ExitCode;
            }
            finally
            {
                StopConsole();
            }
        });
        // ConPTY uses synchronous pipes. Keep writes off the UI and away from the output reader.
        var writeInput = Task.Run(async () =>
        {
            try
            {
                await foreach (var text in pendingInput.Reader.ReadAllAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    input.Write(Encoding.UTF8.GetBytes(text));
                    input.Flush();
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                pendingInput.Writer.TryComplete();
            }
        });

        ExceptionDispatchInfo? displayFailure = null;
        try
        {
            // A separate worker drains the output while ClosePseudoConsole waits.
            await Task.Run(async () =>
            {
                var buffer = new byte[8192];
                int count;
                while ((count = output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (token.IsCancellationRequested || displayFailure is not null)
                        continue;
                    try
                    {
                        await receive(buffer, count);
                    }
                    catch (Exception exception)
                    {
                        displayFailure = ExceptionDispatchInfo.Capture(exception);
                        if (!child.HasExited)
                            child.Kill();
                    }
                }
            });
            var code = await exit;
            displayFailure?.Throw();
            return code;
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                    child.Kill();
                await exit;
            }
            finally
            {
                pendingInput.Writer.TryComplete();
                await writeInput;
                input.Dispose();
                output.Dispose();
                child.Dispose();
            }
        }
    }

    private void StopConsole()
    {
        IntPtr previous;
        lock (gate)
        {
            previous = console;
            console = IntPtr.Zero;
        }
        if (previous != IntPtr.Zero)
            ClosePseudoConsole(previous);
    }

    private static Coord Size(int columns, int rows) =>
        new((short)Math.Clamp(columns, 1, 400), (short)Math.Clamp(rows, 1, 200));

    private static Process StartChild(IntPtr console, string executable, string args)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(attributes, 1, 0, ref size);
            if (!initialized || !UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016,
                console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new StartupInfoEx { Attributes = attributes };
            startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
            var command = new StringBuilder($"\"{executable}\" {args}");
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000,
                IntPtr.Zero, null, ref startup, out var process))
                throw new Win32Exception();

            using var processHandle = new SafeFileHandle(process.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(process.Thread, ownsHandle: true);
            return Process.GetProcessById(process.ProcessId);
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        private readonly short x = x;
        private readonly short y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public short ShowWindow, ReservedLength;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInfo info);
}
