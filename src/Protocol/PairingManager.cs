//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// The exchange that ends with a client trusting this machine: it is how the client learns this
// machine's certificate, without which a stock Moonlight refuses TLS. The PIN cannot be avoided.
internal sealed class PairingManager
{
    // How long the client is kept waiting while the PIN is typed. Step one is the one to hold: the
    // certificate request has no read timeout, while step two is abandoned after five seconds.
    private static readonly TimeSpan PinWait = TimeSpan.FromMinutes(5);

    // What the page handed over for one attempt: the digits, and what to call the device.
    private sealed record Answer(string Pin, string? Name);

    private sealed class Session
    {
        internal required string UniqueId { get; init; }

        // What the device is called: as sent by the client — Moonlight sends the same word from
        // every device — and then what the person typed on the page.
        internal required string DeviceName { get; set; }

        internal required byte[] Salt { get; init; }
        internal required X509Certificate2 ClientCertificate { get; init; }

        internal TaskCompletionSource<Answer> Pin { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal byte[]? AesKey;
        internal byte[]? ServerSecret;
        internal byte[]? ServerChallenge;
        internal byte[]? ClientHash;

        // Set once the client has sent step three, which it does without checking.
        internal bool AnsweredChallenge;

        // Set when the person pressed Cancel on the page, so that the held step can say which
        // of the two kinds of cancellation ended it.
        internal bool Abandoned;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private readonly HostIdentity _identity;
    private readonly ClientStore _clients;

    // Raised when a client starts pairing, so that the user can be told where to type.
    internal event Action<string>? PairingStarted;

    internal PairingManager(HostIdentity identity, ClientStore clients)
    {
        _identity = identity;
        _clients = clients;
    }

    // Whether anything is waiting for a PIN. Used by the page that asks for one.
    internal string? WaitingFor
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.FirstOrDefault(s => !s.Pin.Task.IsCompleted)?.DeviceName;
            }
        }
    }

    // Hands the digits and the device's name to the attempt waiting for them; false when nothing
    // is waiting. Digits are never kept for a later attempt: each has its own PIN and salt.
    internal bool SupplyPin(string pin, string? name)
    {
        lock (_gate)
        {
            var waiting = _sessions.Values.FirstOrDefault(s => !s.Pin.Task.IsCompleted);
            if (waiting is null) return false;

            name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

            Log.Info($"a PIN was entered for \"{waiting.DeviceName}\"" +
                     (name is null ? string.Empty : $", which will be remembered as \"{name}\""));
            waiting.Pin.TrySetResult(new Answer(pin, name));
            return true;
        }
    }

    // Gives up on the attempt waiting for digits, because Cancel was pressed on the page. The held
    // request is refused rather than left parked, so the client stops showing its digits.
    internal bool CancelWaiting()
    {
        lock (_gate)
        {
            var waiting = _sessions.Values.FirstOrDefault(s => !s.Pin.Task.IsCompleted);
            if (waiting is null) return false;

            waiting.Abandoned = true;
            waiting.Pin.TrySetCanceled();
            _sessions.Remove(waiting.UniqueId);

            Log.Info($"pairing with \"{waiting.DeviceName}\" was cancelled from the page");
            return true;
        }
    }

    internal async Task<string> HandleAsync(HttpRequest request, CancellationToken cancel)
    {
        var uniqueId = request.Query("uniqueid") ?? "unknown";
        var phrase = request.Query("phrase");

        // The five steps, told apart by which parameter is present. The protocol has no step
        // number: it is the shape of the request that says where in the exchange it is.
        if (phrase == "getservercert") return await StartSessionAsync(request, uniqueId, cancel);
        if (request.Query("clientchallenge") is { } challenge) return AnswerChallenge(uniqueId, challenge);
        if (request.Query("serverchallengeresp") is { } response) return AnswerChallengeResponse(uniqueId, response);
        if (request.Query("clientpairingsecret") is { } secret) return VerifyClient(uniqueId, secret);
        if (phrase == "pairchallenge") return Finish(uniqueId);

        return Failed("This request is not part of a pairing exchange.");
    }

    // ------------------------------------------------------------------ step 1

    // Records the attempt and holds its answer until the PIN has been typed: the certificate handed
    // over here is what lets the client speak TLS to this machine afterwards.
    private async Task<string> StartSessionAsync(HttpRequest request, string uniqueId,
                                                 CancellationToken cancel)
    {
        var saltText = request.Query("salt");
        var certificateText = request.Query("clientcert");

        if (saltText is null || certificateText is null)
            return Failed("The pairing request is missing its salt or its certificate.");

        X509Certificate2 clientCertificate;
        try
        {
            // Sent as the hexadecimal of the certificate's PEM text — the text, not the DER.
            var pem = Encoding.UTF8.GetString(Convert.FromHexString(certificateText));
            clientCertificate = X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception error)
        {
            return Failed($"The client's certificate could not be read: {error.Message}");
        }

        var name = request.Query("devicename") ?? "a client";
        var session = new Session
        {
            UniqueId = uniqueId,
            DeviceName = name,
            Salt = Convert.FromHexString(saltText),
            ClientCertificate = clientCertificate,
        };

        lock (_gate)
        {
            // A client that starts pairing again abandons what it started before, so the older
            // session is cancelled: its held request is parked on a PIN meant for another exchange.
            if (_sessions.TryGetValue(uniqueId, out var previous))
            {
                previous.Pin.TrySetCanceled();

                // How a wrong PIN is recognised at all: the client checks the answer to step three
                // and, if the digits did not match, stops without telling this server anything.
                if (previous.AnsweredChallenge)
                {
                    Log.Warn(
                        $"\"{name}\" rejected the answer and started again. That means the digits " +
                        "entered here\nwere not the ones it was showing. Each attempt has its own " +
                        "PIN: read the one on\nthe client's screen now, not the one from a moment " +
                        "ago.");
                }
                else
                {
                    Log.Info($"\"{name}\" abandoned its previous pairing attempt and started " +
                             "another; the PIN it is showing now is a new one");
                }
            }

            _sessions[uniqueId] = session;
        }

        Log.Event($"pairing step 1 of 5: \"{name}\" ({uniqueId}) asked for the certificate and is " +
                 "waiting for its PIN");
        PairingStarted?.Invoke(name);

        // Held here, on the one request the client is willing to wait on. Nothing about this
        // machine has been handed over yet, and nothing is until the digits arrive.
        Answer answer;
        try
        {
            answer = await session.Pin.Task.WaitAsync(PinWait, cancel);
        }
        catch (TimeoutException)
        {
            Forget(uniqueId);
            Log.Warn(
                $"\"{name}\" was not given a PIN within {PinWait.TotalMinutes:0} minutes and was " +
                "forgotten.\nAsk the client to pair again, and type the digits it shows then.");
            return Failed("No PIN was entered on the host in time.");
        }
        catch (OperationCanceledException) when (session.Pin.Task.IsCanceled)
        {
            // Either the person pressed Cancel on the page, or the client started a fresh attempt
            // and this one is nobody's business any more.
            return Failed(session.Abandoned
                ? "Pairing was cancelled on the host."
                : "This pairing attempt was replaced by a newer one from the same client.");
        }
        catch (OperationCanceledException)
        {
            // The connection went — Cancel on the client, or the server stopping. The page must
            // stop asking: a box left open takes the next attempt's digits and spends them wrong.
            lock (_gate)
            {
                if (_sessions.TryGetValue(uniqueId, out var current) && current == session)
                    _sessions.Remove(uniqueId);
            }

            Log.Info($"\"{name}\" stopped waiting for its PIN before one was entered");
            return Failed("The client stopped waiting for the PIN.");
        }

        // The key both ends derive independently, made the moment the digits are known. The digits
        // used are logged: a PIN off by one still succeeds here and fails silently on the client.
        session.AesKey = SHA256.HashData(
            session.Salt.Concat(Encoding.ASCII.GetBytes(answer.Pin)).ToArray())[..16];

        if (answer.Name is not null)
        {
            lock (_gate) session.DeviceName = answer.Name;
        }

        Log.Info($"the PIN {answer.Pin} was applied; \"{session.DeviceName}\" is being sent the " +
                 "certificate");

        return Document(xml =>
        {
            xml.WriteElementString("paired", "1");
            xml.WriteElementString("plaincert",
                Convert.ToHexString(Encoding.UTF8.GetBytes(_identity.Certificate.ExportCertificatePem())));
        });
    }

    // ------------------------------------------------------------------ step 2

    // Answers the client's challenge with the key that step one already derived. This is the
    // request the client waits five seconds on and no more, which is why nothing here waits.
    private string AnswerChallenge(string uniqueId, string challengeText)
    {
        var session = Get(uniqueId);
        if (session?.AesKey is null) return Failed("There is no pairing in progress for this client.");

        Log.Info($"pairing step 2 of 5: \"{session.DeviceName}\" sent its challenge");

        byte[] clientChallenge;
        try
        {
            clientChallenge = Decrypt(Convert.FromHexString(challengeText), session.AesKey);
        }
        catch (Exception error)
        {
            Forget(uniqueId);
            return Failed($"The client's challenge could not be read: {error.Message}");
        }

        session.ServerSecret = RandomNumberGenerator.GetBytes(16);
        session.ServerChallenge = RandomNumberGenerator.GetBytes(16);

        var hash = SHA256.HashData(
            Join(clientChallenge, SignatureOf(_identity.Certificate), session.ServerSecret));

        var answer = Encrypt(Join(hash, session.ServerChallenge), session.AesKey);

        return Document(xml =>
        {
            xml.WriteElementString("paired", "1");
            xml.WriteElementString("challengeresponse", Convert.ToHexString(answer));
        });
    }

    // ------------------------------------------------------------------ step 3

    private string AnswerChallengeResponse(string uniqueId, string responseText)
    {
        var session = Get(uniqueId);
        if (session?.AesKey is null || session.ServerSecret is null)
            return Failed("There is no pairing in progress for this client.");

        // Not evidence that the PIN was right: the client sends this step before checking, and a
        // wrong PIN shows up as the attempt stopping here — the absence of step 4 is the message.
        session.AnsweredChallenge = true;
        Log.Info("pairing step 3 of 5: the client sent its response (it has not checked ours yet)");

        try
        {
            // Kept, not checked. What it should contain depends on a secret the client only sends
            // in the step after next, so it is verified there.
            session.ClientHash = Decrypt(Convert.FromHexString(responseText), session.AesKey);
        }
        catch (Exception error)
        {
            Forget(uniqueId);
            return Failed($"The client's response could not be read: {error.Message}");
        }

        using var key = _identity.Certificate.GetRSAPrivateKey()!;
        var signature = key.SignData(session.ServerSecret, HashAlgorithmName.SHA256,
                                     RSASignaturePadding.Pkcs1);

        // The client checks this signature, and then checks the hash it received in step two
        // against one it computes from this secret. A wrong PIN is discovered by the client here.
        return Document(xml =>
        {
            xml.WriteElementString("paired", "1");
            xml.WriteElementString("pairingsecret",
                Convert.ToHexString(Join(session.ServerSecret, signature)));
        });
    }

    // ------------------------------------------------------------------ step 4

    private string VerifyClient(string uniqueId, string secretText)
    {
        var session = Get(uniqueId);
        if (session?.ClientHash is null || session.ServerChallenge is null)
            return Failed("There is no pairing in progress for this client.");

        Log.Info("pairing step 4 of 5: checking the secret the client signed");

        byte[] material;
        try
        {
            material = Convert.FromHexString(secretText);
        }
        catch (Exception error)
        {
            Forget(uniqueId);
            return Failed($"The client's secret could not be read: {error.Message}");
        }

        if (material.Length <= 16)
        {
            Forget(uniqueId);
            return Failed("The client's secret is too short to contain a signature.");
        }

        var clientSecret = material[..16];
        var clientSignature = material[16..];

        using var clientKey = session.ClientCertificate.GetRSAPublicKey();
        if (clientKey is null ||
            !clientKey.VerifyData(clientSecret, clientSignature, HashAlgorithmName.SHA256,
                                  RSASignaturePadding.Pkcs1))
        {
            Forget(uniqueId);
            Log.Warn($"\"{session.DeviceName}\" failed pairing: its secret was not signed by the " +
                     "certificate it presented.");
            return Failed("The client's secret was not signed by the certificate it presented.");
        }

        var expected = SHA256.HashData(
            Join(session.ServerChallenge, SignatureOf(session.ClientCertificate), clientSecret));

        if (!CryptographicOperations.FixedTimeEquals(expected, session.ClientHash))
        {
            Forget(uniqueId);

            // This is what a wrong PIN looks like from here, and it is worth saying so plainly:
            // everything up to this point succeeded, and the message the client shows is vague.
            Log.Warn($"\"{session.DeviceName}\" failed pairing: the PIN entered on this machine did " +
                     "not match the one the client is showing. Ask it to pair again.");
            return Failed("The PIN entered on the host does not match the one this client is showing.");
        }

        // Admitted, with nothing to approve: the first client to ask is the client that is trusted,
        // under the name the person gave it on the page.
        _clients.Admit(session.UniqueId, session.DeviceName, session.ClientCertificate);

        return Document(xml => xml.WriteElementString("paired", "1"));
    }

    // ------------------------------------------------------------------ step 5

    private string Finish(string uniqueId)
    {
        Log.Info("pairing step 5 of 5: done");

        Forget(uniqueId);
        return Document(xml => xml.WriteElementString("paired", "1"));
    }

    // ------------------------------------------------------------------ the pieces

    private Session? Get(string uniqueId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(uniqueId);
    }

    private void Forget(string uniqueId)
    {
        lock (_gate) _sessions.Remove(uniqueId);
    }

    // The signature bytes of a certificate — the last field of the X.509 structure. Both ends mix
    // it into their hashes, binding the exchange to the exact certificates presented.
    private static byte[] SignatureOf(X509Certificate2 certificate)
    {
        var certificateSequence = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();

        certificateSequence.ReadEncodedValue();   // tbsCertificate
        certificateSequence.ReadEncodedValue();   // signatureAlgorithm

        return certificateSequence.ReadBitString(out _);
    }

    // Electronic codebook, no padding: every message here is a whole number of blocks and both
    // ends encrypt block by block. The protocol's choice, not one this server would make.
    private static byte[] Encrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Decrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Join(params byte[][] parts)
    {
        var joined = new byte[parts.Sum(part => part.Length)];
        var offset = 0;

        foreach (var part in parts)
        {
            part.CopyTo(joined, offset);
            offset += part.Length;
        }

        return joined;
    }

    private static string Document(Action<System.Xml.XmlWriter> body) =>
        GameStreamServer.BuildDocument(body);

    private static string Failed(string reason) =>
        GameStreamServer.BuildDocument(xml =>
        {
            xml.WriteElementString("paired", "0");
            xml.WriteElementString("status_message", reason);
        });
}
