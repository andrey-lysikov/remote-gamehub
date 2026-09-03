//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using RemoteGameHub.Library;
using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The five-step exchange, driven from the client's side exactly as Moonlight drives it — the same
// key derivation, hashes and signatures — so a change to the server's half is caught here.
public class PairingTests
{
    // What Moonlight holds during one attempt.
    private sealed class Client : IDisposable
    {
        internal readonly X509Certificate2 Certificate = ClientStoreTests.ClientCertificate();
        internal readonly byte[] Salt = RandomNumberGenerator.GetBytes(16);
        internal readonly string Pin;
        internal readonly byte[] AesKey;
        internal readonly string UniqueId = "0123456789ABCDEF";

        internal Client(string pin, string? keyFromPin = null)
        {
            Pin = pin;
            AesKey = SHA256.HashData(Salt.Concat(Encoding.ASCII.GetBytes(keyFromPin ?? pin)).ToArray())[..16];
        }

        internal string Step1 =>
            $"devicename=roth&updateState=1&phrase=getservercert&salt={Convert.ToHexString(Salt)}" +
            $"&clientcert={Convert.ToHexString(Encoding.UTF8.GetBytes(Certificate.ExportCertificatePem()))}";

        internal byte[] Aes(byte[] data, bool encrypt)
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = AesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        internal byte[] Sign(byte[] data)
        {
            using var key = Certificate.GetRSAPrivateKey()!;
            return key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public void Dispose() => Certificate.Dispose();
    }

    private sealed class Host : IDisposable
    {
        internal readonly TestFolder Folder = new();
        internal readonly Database Database;
        internal readonly ClientStore Clients;
        internal readonly HostIdentity Identity;
        internal readonly PairingManager Pairing;

        internal Host()
        {
            Database = Database.Open(Folder.Path);
            Clients = new ClientStore(Database);
            Identity = HostIdentity.Load(Folder.Path, "Test host");
            Pairing = new PairingManager(Identity, Clients);
        }

        internal async Task<XElement> Ask(Client client, string query)
        {
            var request = await HttpRequest.ReadAsync(new MemoryStream(Encoding.ASCII.GetBytes(
                $"GET /pair?uniqueid={client.UniqueId}&{query} HTTP/1.1\r\nHost: x\r\n\r\n")),
                CancellationToken.None);

            var answer = await Pairing.HandleAsync(request!, CancellationToken.None);
            return XDocument.Parse(answer).Root!;
        }

        // Step one, with the PIN and the name typed on the page while it waits.
        internal async Task<XElement> Step1WithPin(Client client, string pin, string? name)
        {
            var step1 = Ask(client, client.Step1);

            // The page's poll, sped up: wait until the exchange says something is waiting.
            for (var i = 0; i < 200 && Pairing.WaitingFor is null; i++) await Task.Delay(10);
            Assert.Equal("roth", Pairing.WaitingFor);

            Assert.True(Pairing.SupplyPin(pin, name));
            return await step1;
        }

        public void Dispose()
        {
            Database.Dispose();
            Folder.Dispose();
        }
    }

    private static byte[] SignatureOf(X509Certificate2 certificate)
    {
        var sequence = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        sequence.ReadEncodedValue();
        sequence.ReadEncodedValue();
        return sequence.ReadBitString(out _);
    }

    private static string Text(XElement root, string name) => root.Element(name)?.Value ?? string.Empty;

    [Fact]
    public async Task The_whole_exchange_admits_the_device_under_the_typed_name()
    {
        using var host = new Host();
        using var client = new Client("4711");

        // Step 1: the certificate is handed over only once the digits are typed.
        var step1 = await host.Step1WithPin(client, "4711", "Living-room TV");
        Assert.Equal("1", Text(step1, "paired"));
        var serverCertificate = X509Certificate2.CreateFromPem(
            Encoding.UTF8.GetString(Convert.FromHexString(Text(step1, "plaincert"))));
        Assert.Equal(host.Identity.Certificate.Thumbprint, serverCertificate.Thumbprint);
        Assert.Null(host.Pairing.WaitingFor);

        // Step 2: the challenge, answered under the key both sides derived from the PIN.
        var clientChallenge = RandomNumberGenerator.GetBytes(16);
        var step2 = await host.Ask(client,
            $"devicename=roth&updateState=1&clientchallenge={Convert.ToHexString(client.Aes(clientChallenge, true))}");
        Assert.Equal("1", Text(step2, "paired"));

        var answer = client.Aes(Convert.FromHexString(Text(step2, "challengeresponse")), false);
        Assert.Equal(48, answer.Length);
        var serverHash = answer[..32];
        var serverChallenge = answer[32..];

        // Step 3: the client's hash over the server's challenge, and the server's secret back.
        var clientSecret = RandomNumberGenerator.GetBytes(16);
        var clientHash = SHA256.HashData(serverChallenge
            .Concat(SignatureOf(client.Certificate)).Concat(clientSecret).ToArray());
        var step3 = await host.Ask(client,
            $"devicename=roth&updateState=1&serverchallengeresp={Convert.ToHexString(client.Aes(clientHash, true))}");
        Assert.Equal("1", Text(step3, "paired"));

        var secretAndSignature = Convert.FromHexString(Text(step3, "pairingsecret"));
        var serverSecret = secretAndSignature[..16];
        var serverSignature = secretAndSignature[16..];

        // What Moonlight checks here, and where a wrong PIN shows up on its screen.
        using (var serverKey = serverCertificate.GetRSAPublicKey()!)
        {
            Assert.True(serverKey.VerifyData(serverSecret, serverSignature, HashAlgorithmName.SHA256,
                                             RSASignaturePadding.Pkcs1));
        }
        Assert.Equal(
            SHA256.HashData(clientChallenge.Concat(SignatureOf(serverCertificate)).Concat(serverSecret).ToArray()),
            serverHash);

        // Step 4: the client's secret, signed — and the admission.
        Assert.Null(host.Clients.Find(client.Certificate));
        var step4 = await host.Ask(client,
            $"devicename=roth&updateState=1&clientpairingsecret={Convert.ToHexString(clientSecret.Concat(client.Sign(clientSecret)).ToArray())}");
        Assert.Equal("1", Text(step4, "paired"));

        var known = host.Clients.Find(client.Certificate);
        Assert.NotNull(known);
        Assert.Equal("Living-room TV", known.Name);

        // Step 5, over TLS in real life: done.
        var step5 = await host.Ask(client, "devicename=roth&updateState=1&phrase=pairchallenge");
        Assert.Equal("1", Text(step5, "paired"));
    }

    [Fact]
    public async Task An_empty_name_leaves_the_clients_own()
    {
        using var host = new Host();
        using var client = new Client("1234");

        await host.Step1WithPin(client, "1234", "   ");
        await FinishFromStep2(host, client);

        Assert.Equal("roth", host.Clients.Find(client.Certificate)?.Name);
    }

    [Fact]
    public async Task A_wrong_pin_is_caught_at_step_four_and_nothing_is_admitted()
    {
        using var host = new Host();
        // The client derives its key from 1234; the person types 1235.
        using var client = new Client("1234");

        await host.Step1WithPin(client, "1235", "TV");

        var clientChallenge = RandomNumberGenerator.GetBytes(16);
        var step2 = await host.Ask(client,
            $"clientchallenge={Convert.ToHexString(client.Aes(clientChallenge, true))}");
        Assert.Equal("1", Text(step2, "paired"));   // the server cannot tell yet

        var serverChallenge = client.Aes(Convert.FromHexString(Text(step2, "challengeresponse")), false)[32..];
        var clientSecret = RandomNumberGenerator.GetBytes(16);
        var clientHash = SHA256.HashData(serverChallenge
            .Concat(SignatureOf(client.Certificate)).Concat(clientSecret).ToArray());
        await host.Ask(client, $"serverchallengeresp={Convert.ToHexString(client.Aes(clientHash, true))}");

        var step4 = await host.Ask(client,
            $"clientpairingsecret={Convert.ToHexString(clientSecret.Concat(client.Sign(clientSecret)).ToArray())}");

        Assert.Equal("0", Text(step4, "paired"));
        Assert.Null(host.Clients.Find(client.Certificate));
    }

    [Fact]
    public async Task A_step_without_a_first_is_refused()
    {
        using var host = new Host();
        using var client = new Client("0000");

        var answer = await host.Ask(client, "clientchallenge=" + new string('0', 32));

        Assert.Equal("0", Text(answer, "paired"));
        Assert.False(host.Pairing.SupplyPin("0000", null));
    }

    [Fact]
    public async Task A_client_that_starts_again_cancels_its_earlier_attempt()
    {
        using var host = new Host();
        using var client = new Client("2222");

        var first = host.Ask(client, client.Step1);
        for (var i = 0; i < 200 && host.Pairing.WaitingFor is null; i++) await Task.Delay(10);

        var second = await host.Step1WithPin(client, "2222", "TV");

        Assert.Equal("0", Text(await first, "paired"));
        Assert.Equal("1", Text(second, "paired"));
    }

    [Fact]
    public async Task A_connection_that_goes_away_stops_the_page_asking()
    {
        using var host = new Host();
        using var client = new Client("3333");

        using var gone = new CancellationTokenSource();
        var request = await HttpRequest.ReadAsync(new MemoryStream(Encoding.ASCII.GetBytes(
            $"GET /pair?uniqueid={client.UniqueId}&{client.Step1} HTTP/1.1\r\n\r\n")), CancellationToken.None);
        var held = host.Pairing.HandleAsync(request!, gone.Token);

        for (var i = 0; i < 200 && host.Pairing.WaitingFor is null; i++) await Task.Delay(10);
        Assert.NotNull(host.Pairing.WaitingFor);

        gone.Cancel();
        var answer = XDocument.Parse(await held).Root!;

        Assert.Equal("0", Text(answer, "paired"));
        Assert.Null(host.Pairing.WaitingFor);
        Assert.False(host.Pairing.SupplyPin("3333", null));
    }

    // Steps two to four with the PIN the client itself holds, which is the happy path.
    private static async Task FinishFromStep2(Host host, Client client)
    {
        var clientChallenge = RandomNumberGenerator.GetBytes(16);
        var step2 = await host.Ask(client,
            $"clientchallenge={Convert.ToHexString(client.Aes(clientChallenge, true))}");
        var serverChallenge = client.Aes(Convert.FromHexString(Text(step2, "challengeresponse")), false)[32..];

        var clientSecret = RandomNumberGenerator.GetBytes(16);
        var clientHash = SHA256.HashData(serverChallenge
            .Concat(SignatureOf(client.Certificate)).Concat(clientSecret).ToArray());
        await host.Ask(client, $"serverchallengeresp={Convert.ToHexString(client.Aes(clientHash, true))}");

        var step4 = await host.Ask(client,
            $"clientpairingsecret={Convert.ToHexString(clientSecret.Concat(client.Sign(clientSecret)).ToArray())}");
        Assert.Equal("1", Text(step4, "paired"));
    }
}
