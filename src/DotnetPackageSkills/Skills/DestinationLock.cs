using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotnetPackageSkills.Skills;

/// <summary>Serializes cooperating tool processes from ownership checks through manifest persistence.</summary>
internal sealed class DestinationLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private DestinationLock(Mutex mutex) => _mutex = mutex;

    public static DestinationLock Acquire(string destination, TimeSpan? timeout = null)
    {
        var name = NameFor(destination);
        var mutex = new Mutex(initiallyOwned: false, name);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException error)
            {
                acquired = true;
                throw new PackageSkillsException(
                    $"A previous operation on '{destination}' was interrupted. " +
                    "Check the destination and its manifest before trying again; no changes were made by this operation.",
                    error);
            }

            if (!acquired)
            {
                throw new PackageSkillsException(
                    $"Another operation is using the skills destination '{destination}'. " +
                    "Wait for it to finish and try again. No skills were changed.");
            }

            if (!name.Equals(NameFor(destination), StringComparison.Ordinal))
            {
                throw new PackageSkillsException(
                    $"The skills destination '{destination}' changed while waiting for another operation. " +
                    "Run the command again to review its current location. No skills were changed.");
            }

            return new DestinationLock(mutex);
        }
        catch
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
            throw;
        }
    }

    internal static string NameFor(string destination)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (OperatingSystem.IsWindows())
        {
            return MutexName(CanonicalWindowsPath(full).ToUpperInvariant(), windows: true);
        }

        var root = Path.GetPathRoot(full)!;
        var canonical = root;
        foreach (var part in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            canonical = Path.Combine(canonical, part);
            var directory = new DirectoryInfo(canonical);
            if (directory.Exists && directory.LinkTarget is not null)
            {
                canonical = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new PackageSkillsException($"Could not resolve the skills destination '{destination}'.");
            }
        }

        canonical = Path.TrimEndingDirectorySeparator(canonical);
        return MutexName(canonical, windows: false);
    }

    private static string MutexName(string canonical, bool windows)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return (windows ? @"Global\" : string.Empty) + "dotnet-package-skills-" + hash;
    }

    [SupportedOSPlatform("windows")]
    private static string CanonicalWindowsPath(string full)
    {
        var missing = new Stack<string>();
        var existing = new DirectoryInfo(full);
        while (!existing.Exists)
        {
            missing.Push(existing.Name);
            existing = existing.Parent
                ?? throw new PackageSkillsException($"Could not resolve the skills destination '{full}'.");
        }

        // The OS resolves device prefixes, short names, drive mappings, and junctions to
        // the same path. Resolve only the existing parent so previews create no directories.
        var openPath = existing.FullName;
        if (!openPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !openPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            openPath = openPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + openPath[2..]
                : @"\\?\" + openPath;
        }

        using var handle = CreateFile(
            openPath, 0, 7, nint.Zero, 3, 0x02000000, nint.Zero);
        if (handle.IsInvalid)
        {
            throw PathError(full);
        }

        var path = new StringBuilder(256);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0)
        {
            throw PathError(full);
        }

        if (length >= path.Capacity)
        {
            path = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity)
            {
                throw PathError(full);
            }
        }

        var canonical = path.ToString();
        if (canonical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            canonical = @"\\" + canonical[8..];
        }
        else if (canonical.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            canonical = canonical[4..];
        }

        foreach (var component in missing)
        {
            canonical = Path.Combine(canonical, component);
        }

        return Path.TrimEndingDirectorySeparator(canonical);
    }

    private static IOException PathError(string path) => new(
        $"Could not resolve the skills destination '{path}'.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
