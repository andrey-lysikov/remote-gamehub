//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// Who this machine says it is, and the certificate it says it with. Moonlight's host identifier
// must not change between runs, so it is derived from the certificate kept beside the config.
internal sealed class HostIdentity
{
    // Self-signed and long-lived. Nothing checks who signed it: it is here to encrypt the
    // connection, not to prove anything on a local network.
    private const int ValidYears = 20;

    private const string Subject = "CN=Remote Game Hub";

    internal X509Certificate2 Certificate { get; }

    // The identifier clients remember this machine by. Stable for the life of the file.
    internal string UniqueId { get; }

    // The name shown in the client's list.
    internal string HostName { get; }

    private HostIdentity(X509Certificate2 certificate, string hostName)
    {
        Certificate = certificate;
        HostName = hostName;

        // Derived from the certificate rather than stored separately: two files that are supposed
        // to agree eventually will not, and the one that is wrong is the one nobody looks at.
        UniqueId = Convert.ToHexString(SHA256.HashData(certificate.RawData))[..32];
    }

    internal static HostIdentity Load(string directory, string configuredName)
    {
        var path = Path.Combine(directory, AppParameters.Identity.FileBase + ".pem");
        var certificate = ReadOrCreate(path);

        var name = string.Equals(configuredName, "auto", StringComparison.OrdinalIgnoreCase)
            ? Environment.MachineName
            : configuredName;

        var identity = new HostIdentity(certificate, name);
        Log.Info($"host identity: \"{name}\", id {identity.UniqueId}, " +
                 $"certificate valid until {certificate.NotAfter:yyyy-MM-dd}");

        return identity;
    }

    private static X509Certificate2 ReadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return MakeUsableForTls(X509Certificate2.CreateFromPemFile(path));
            }
            catch (Exception error)
            {
                // A certificate that cannot be read is replaced rather than fatal — but every
                // client that remembered the old one will treat this machine as a new one.
                Log.Warn($"the certificate at {path} could not be read ({error.Message}); " +
                         "a new one is being made. Clients will see this machine as a new host.");
            }
        }

        var created = Create();
        Save(created, path);
        Log.Event($"a new certificate was written to {path}");

        return MakeUsableForTls(created);
    }

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256,
                                             RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));   // server authentication

        // Backdated by a day: a client whose clock is a little behind would otherwise reject a
        // certificate that is not valid yet, and report something unrelated to the clock.
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(ValidYears));
    }

    private static void Save(X509Certificate2 certificate, string path)
    {
        using var key = certificate.GetRSAPrivateKey()!;

        var text = new StringBuilder();
        text.AppendLine(certificate.ExportCertificatePem());
        text.AppendLine(key.ExportPkcs8PrivateKeyPem());

        File.WriteAllText(path, text.ToString());
    }

    // A certificate built in memory carries an ephemeral key the Windows TLS stack cannot use: the
    // handshake fails with "the credentials supplied to the package were not recognized".
    private static X509Certificate2 MakeUsableForTls(X509Certificate2 certificate)
    {
        var exported = certificate.Export(X509ContentType.Pkcs12);
        certificate.Dispose();

        return X509CertificateLoader.LoadPkcs12(exported, password: null);
    }
}
