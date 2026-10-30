namespace OmniCore.Infrastructure;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal interface ICredentialKeyStore
{
    byte[]? Load(CancellationToken cancellationToken);
    void Save(byte[] key, CancellationToken cancellationToken);
    void Delete(CancellationToken cancellationToken);
}

/// <summary>Stores only the credential-file wrapping key in the user's native Secret Service.</summary>
internal sealed partial class LinuxSecretServiceKeyStore(string credentialPath) : ICredentialKeyStore
{
    private readonly string _scope = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(credentialPath))));

    public byte[]? Load(CancellationToken cancellationToken)
    {
        using var call = new NativeCall(_scope, cancellationToken);
        var password = Native.Lookup(0, call.Attributes, call.Cancellable, out var error);
        try
        {
            call.Check(error);
            if (password == 0) return null;
            try
            {
                var bytes = Convert.FromBase64String(Marshal.PtrToStringUTF8(password)!);
                if (bytes.Length == 32) return bytes;
                CryptographicOperations.ZeroMemory(bytes);
                throw new CryptographicException("The credential wrapping key is invalid.");
            }
            catch (FormatException)
            { throw new CryptographicException("The credential wrapping key is invalid."); }
        }
        finally { if (password != 0) Native.FreePassword(password); }
    }

    public void Save(byte[] key, CancellationToken cancellationToken)
    {
        if (key.Length != 32) throw new ArgumentException("A 32-byte key is required.", nameof(key));
        using var call = new NativeCall(_scope, cancellationToken);
        var saved = Native.Store(0, call.Attributes, null, "OmniCore credential encryption key",
            Convert.ToBase64String(key), call.Cancellable, out var error);
        call.Check(error);
        if (saved == 0) throw new CryptographicException("Secret Service did not store the wrapping key.");
    }

    public void Delete(CancellationToken cancellationToken)
    {
        using var call = new NativeCall(_scope, cancellationToken);
        _ = Native.Clear(0, call.Attributes, call.Cancellable, out var error);
        call.Check(error);
    }

    private sealed class NativeCall : IDisposable
    {
        private static readonly Lazy<nint> Glib = new(() => NativeLibrary.Load("libglib-2.0.so.0"));
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenRegistration _cancellation;
        public nint Attributes { get; }
        public nint Cancellable { get; }

        public NativeCall(string scope, CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Secret Service requires Linux.");
            _caller = caller;
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Cancellable = Native.NewCancellable();
            try
            {
                Attributes = Native.NewTable(NativeLibrary.GetExport(Glib.Value, "g_str_hash"),
                    NativeLibrary.GetExport(Glib.Value, "g_str_equal"),
                    NativeLibrary.GetExport(Glib.Value, "g_free"), NativeLibrary.GetExport(Glib.Value, "g_free"));
                _ = Native.Insert(Attributes, Native.Duplicate("application"), Native.Duplicate("org.omnicore.credentials"));
                _ = Native.Insert(Attributes, Native.Duplicate("profile"), Native.Duplicate(scope));
                _ = Native.Insert(Attributes, Native.Duplicate("purpose"), Native.Duplicate("wrapping-key-v1"));
                _cancellation = _deadline.Token.Register(() => Native.Cancel(Cancellable));
            }
            catch
            {
                if (Attributes != 0) Native.DestroyTable(Attributes);
                Native.Unref(Cancellable);
                _deadline.Dispose();
                throw;
            }
        }

        public void Check(nint error)
        {
            if (error != 0) Native.FreeError(error);
            _caller.ThrowIfCancellationRequested();
            if (_deadline.IsCancellationRequested) throw new TimeoutException("Secret Service did not respond within ten seconds.");
            if (error != 0)
                throw new CryptographicException("Secret Service is unavailable or locked; no file-key fallback was used.");
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            Native.DestroyTable(Attributes);
            Native.Unref(Cancellable);
            _deadline.Dispose();
        }
    }

    private static partial class Native
    {
        [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_password_lookupv_sync")]
        internal static partial nint Lookup(nint schema, nint attributes, nint cancellable, out nint error);
        [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_password_storev_sync", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Store(nint schema, nint attributes, string? collection, string label,
            string password, nint cancellable, out nint error);
        [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_password_clearv_sync")]
        internal static partial int Clear(nint schema, nint attributes, nint cancellable, out nint error);
        [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_password_free")]
        internal static partial void FreePassword(nint password);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_new_full")]
        internal static partial nint NewTable(nint hash, nint equal, nint destroyKey, nint destroyValue);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_insert")]
        internal static partial int Insert(nint table, nint key, nint value);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_destroy")]
        internal static partial void DestroyTable(nint table);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_strdup", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint Duplicate(string value);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")]
        internal static partial void FreeError(nint error);
        [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")]
        internal static partial nint NewCancellable();
        [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")]
        internal static partial void Cancel(nint cancellable);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
        internal static partial void Unref(nint value);
    }
}
