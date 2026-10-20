using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VRLinkPublisher.Domain;

#pragma warning disable CA1416 // guarded by OperatingSystem.IsWindows()
namespace VRLinkPublisher.Services;

public interface ISecretProtector
{
    byte[] Protect(string plain);
    string? Unprotect(byte[]? blob);
}

/// <summary>Windows DPAPI (ProtectedData). Tokens/secrets are never stored in plain text.</summary>
public sealed class DpapiSecretProtector(IOptions<PublisherOptions> options) : ISecretProtector
{
    // Fixed app entropy: a blob is only readable by this app (plus the DPAPI scope rule).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VRLinkPublisher.v1");

    private DataProtectionScope Scope =>
        string.Equals(options.Value.DpapiScope, "CurrentUser", StringComparison.OrdinalIgnoreCase)
            ? DataProtectionScope.CurrentUser
            : DataProtectionScope.LocalMachine;

    public byte[] Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        return ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, Scope);
    }

    public string? Unprotect(byte[]? blob)
    {
        if (blob is null || blob.Length == 0) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, Scope)); }
        catch (CryptographicException) { return null; } // other user/machine/scope: treat as not connected
    }
}
