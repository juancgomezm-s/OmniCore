param([ValidateSet('Inspect', 'Enable', 'Restore')][string]$Action = 'Inspect')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# NUL is a kernel device, not a workspace file. Fresh Windows runners can deny
# AppContainer access to it; Git opens it even when all stdio handles are valid.
# Use handle-based security APIs; named file ACL APIs do not update this device.
# Reference: https://github.com/libuv/libuv/blob/v1.x/test/appcontainer.c
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

public static class OmniCiNullDevice
{
    [StructLayout(LayoutKind.Sequential)] struct Trustee
    {
        public IntPtr Multiple;
        public uint Operation, Form, Type;
        public IntPtr Sid;
    }
    [StructLayout(LayoutKind.Sequential)] struct ExplicitAccess
    {
        public uint Permissions, Mode, Inheritance;
        public Trustee Trustee;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr attributes, uint creation, uint flags, IntPtr template);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    static extern uint SetSecurityInfo(SafeFileHandle handle, uint type, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
    static extern uint SetEntriesInAclW(uint count, ref ExplicitAccess entry, IntPtr oldAcl, out IntPtr newAcl);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);

    static SafeFileHandle Open(bool write)
    {
        var h = CreateFileW(@"\\.\NUL", write ? 0x60000u : 0x20000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open NUL security");
        return h;
    }
    static void Check(uint status)
    {
        if (status != 0) throw new Win32Exception(checked((int)status));
    }
    public static byte[] Read()
    {
        using (var h = Open(false))
        {
            IntPtr descriptor;
            Check(GetSecurityInfo(h, 1, 4, out _, out _, out _, out _, out descriptor));
            try
            {
                var bytes = new byte[checked((int)GetSecurityDescriptorLength(descriptor))];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { LocalFree(descriptor); }
        }
    }
    public static string Describe(byte[] descriptor)
    {
        return new RawSecurityDescriptor(descriptor, 0).GetSddlForm(AccessControlSections.Access);
    }
    public static void Grant()
    {
        var sid = new SecurityIdentifier("S-1-15-2-1"); // ALL APPLICATION PACKAGES
        var sidBytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(sidBytes, 0);
        var pinnedSid = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        IntPtr descriptor = IntPtr.Zero, acl = IntPtr.Zero;
        try
        {
            using (var h = Open(true))
            {
                IntPtr oldAcl;
                Check(GetSecurityInfo(h, 1, 4, out _, out _, out oldAcl, out _, out descriptor));
                if (oldAcl == IntPtr.Zero) throw new InvalidOperationException("Refusing to replace a null DACL");
                var entry = new ExplicitAccess { Permissions = 0xC0000000u, Mode = 1,
                    Trustee = new Trustee { Form = 0, Type = 5, Sid = pinnedSid.AddrOfPinnedObject() } };
                Check(SetEntriesInAclW(1, ref entry, oldAcl, out acl));
                Check(SetSecurityInfo(h, 1, 4, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero));
            }
        }
        finally
        {
            if (acl != IntPtr.Zero) LocalFree(acl);
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            pinnedSid.Free();
        }
    }
    public static void Restore(byte[] descriptor)
    {
        var dacl = new RawSecurityDescriptor(descriptor, 0).DiscretionaryAcl;
        if (dacl == null) throw new InvalidOperationException("Refusing to restore a null DACL");
        var bytes = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            using (var h = Open(true))
                Check(SetSecurityInfo(h, 1, 4, IntPtr.Zero, IntPtr.Zero, memory, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
'@

if ($Action -eq 'Inspect') {
    Write-Output ([OmniCiNullDevice]::Describe([OmniCiNullDevice]::Read()))
    exit 0
}
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP -or -not $env:GITHUB_ENV) {
    throw 'NUL device preparation is restricted to an ephemeral GitHub Actions runner.'
}
$snapshotPath = Join-Path $env:RUNNER_TEMP 'omnicore-original-nul-security.bin'
$volumeRoot = [IO.Path]::GetPathRoot([Environment]::GetFolderPath([Environment+SpecialFolder]::System))
if ($Action -eq 'Enable') {
    if (Test-Path -LiteralPath $snapshotPath) { throw 'Original NUL security was already saved.' }
    $original = [OmniCiNullDevice]::Read()
    if ($null -eq [System.Security.AccessControl.RawSecurityDescriptor]::new($original, 0).DiscretionaryAcl) {
        throw 'Refusing to replace a null NUL-device DACL.'
    }
    Write-Output ('NUL before: ' + [OmniCiNullDevice]::Describe($original))
    [IO.File]::WriteAllBytes($snapshotPath, $original)
    'OMNICORE_CI_NUL_PREPARED=true' | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    [OmniCiNullDevice]::Grant()
    Write-Output ('NUL prepared: ' + [OmniCiNullDevice]::Describe([OmniCiNullDevice]::Read()))
    # Git resolves its cwd through parent directory metadata. A fresh, synthetic
    # temp tree keeps that host prerequisite away from the runner user profile.
    # ContainerInherit without ObjectInherit applies to directories, never files.
    $packageSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-15-2-1')
    $testTemp = Join-Path $volumeRoot ('omnicore-ci-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $testTemp) { throw 'Synthetic CI temp directory already exists.' }
    $tempInfo = [IO.Directory]::CreateDirectory($testTemp)
    $tempSecurity = [IO.FileSystemAclExtensions]::GetAccessControl($tempInfo)
    $directoryRule = [System.Security.AccessControl.FileSystemAccessRule]::new($packageSid,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $tempSecurity.AddAccessRule($directoryRule)
    [IO.FileSystemAclExtensions]::SetAccessControl($tempInfo, $tempSecurity)
    Write-Output "Synthetic CI temp: $testTemp; directory metadata only; no file inheritance"
    foreach ($name in @('TEMP', 'TMP')) {
        $originalPath = [Environment]::GetEnvironmentVariable($name)
        "OMNICORE_CI_ORIGINAL_$name=$originalPath" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
        "$name=$testTemp" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    }
} else {
    [OmniCiNullDevice]::Restore([IO.File]::ReadAllBytes($snapshotPath))
    Write-Output ('NUL restored: ' + [OmniCiNullDevice]::Describe([OmniCiNullDevice]::Read()))
    foreach ($name in @('TEMP', 'TMP')) {
        $originalPath = [Environment]::GetEnvironmentVariable("OMNICORE_CI_ORIGINAL_$name")
        if ($originalPath) { "$name=$originalPath" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8 }
    }
}
