using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Raizen.Server.Setup.Installer;

public static class CertGenerator
{
    /// <summary>
    /// Generates a self-signed TLS certificate valid for 5 years.
    /// SANs include the given hostname, the machine name, localhost, loopback,
    /// and ALL current non-loopback IPv4 addresses on the machine so that
    /// agents connecting by IP (e.g. https://192.168.1.74:5001) pass TLS
    /// hostname validation even without certificate pinning.
    /// Returns (pfxBytes, thumbprintSha256).
    /// </summary>
    public static (byte[] PfxBytes, string ThumbprintSha256) GenerateSelfSignedCert(
        string hostname, string pfxPassword)
    {
        using var rsa = RSA.Create(2048);

        var req = new CertificateRequest(
            $"CN={hostname}, O=Raizen Security",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Key usage: Digital Signature + Key Encipherment
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        // EKU: TLS server authentication
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        // SAN: hostname + machine name + localhost + all local LAN IPs
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        if (!hostname.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        // Add every non-loopback IPv4 address so agents connecting via LAN IP
        // get a valid cert SAN match — eliminates RemoteCertificateNameMismatch.
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    san.AddIpAddress(ua.Address);
            }
        }

        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        var pfxBytes = cert.Export(X509ContentType.Pfx, pfxPassword);
        // Return SHA-256 thumbprint — this is what the agent's TLS pinning callback uses.
        var thumbprintSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
        return (pfxBytes, thumbprintSha256);
    }

    /// <summary>
    /// Installs the PFX into the LocalMachine certificate stores
    /// (Personal + Trusted Root) so Windows and endpoint agents trust it.
    /// </summary>
    public static void InstallToMachineStore(byte[] pfxBytes, string pfxPassword)
    {
        var cert = new X509Certificate2(pfxBytes, pfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

        using var myStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        myStore.Open(OpenFlags.ReadWrite);
        myStore.Add(cert);

        using var rootStore = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        rootStore.Open(OpenFlags.ReadWrite);
        rootStore.Add(cert);
    }

    /// <summary>
    /// Generates an RSA-2048 key pair for poll response signing.
    /// Returns (privateKeyPem, publicKeyPem).
    /// </summary>
    public static (string PrivatePem, string PublicPem) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportRSAPublicKeyPem());
    }
}
