using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Creates ACL-protected Worker named pipes and verifies the connected client computer before any request data is read.
/// </summary>
public sealed class WindowsWorkerPipeFactory
{
    private const int ErrorPipeLocal = 229;

    /// <summary>
    /// Creates one asynchronous pipe server instance for SYSTEM, administrators, and local Users while denying anonymous access.
    /// </summary>
    public NamedPipeServerStream Create(string pipeName, int maximumServerInstances = 16)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        }

        if (maximumServerInstances is < 1 or > 254)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumServerInstances),
                "Named pipe server instances must be between 1 and 254.");
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            pipeName.Trim(),
            PipeDirection.InOut,
            maximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    /// <summary>
    /// Returns whether the pipe is connected from this computer; lookup failures are rejected rather than trusted.
    /// </summary>
    public bool IsLocalAuthorizedClient(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            return false;
        }

        var clientComputerName = new StringBuilder(256);
        if (!GetNamedPipeClientComputerName(
                pipe.SafePipeHandle.DangerousGetHandle(),
                clientComputerName,
                (uint)clientComputerName.Capacity))
        {
            // Windows reports ERROR_PIPE_LOCAL instead of a computer name for a local-only connection.
            return Marshal.GetLastWin32Error() == ErrorPipeLocal;
        }

        var normalizedClientName = clientComputerName
            .ToString()
            .Trim()
            .TrimStart('\\');
        var clientHostName = normalizedClientName.Split('.', 2)[0];
        var localHostName = Environment.MachineName.Split('.', 2)[0];
        return string.Equals(clientHostName, localHostName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(clientHostName, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(clientHostName, ".", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(
        IntPtr pipeHandle,
        StringBuilder clientComputerName,
        uint clientComputerNameLength);
}
