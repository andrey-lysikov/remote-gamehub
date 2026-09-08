//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteGameHub.Library;
using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The devices this machine remembers: by certificate, under the name they were given.
public class ClientStoreTests
{
    internal static X509Certificate2 ClientCertificate(string name = "NVIDIA GameStream Client")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256,
                                             RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void A_client_is_found_by_its_certificate_and_listed_under_its_name()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var clients = new ClientStore(database);

        using var certificate = ClientCertificate();
        Assert.Null(clients.Find(certificate));

        clients.Admit("0123456789ABCDEF", "Living-room TV", certificate);

        var known = clients.Find(certificate);
        Assert.NotNull(known);
        Assert.Equal("Living-room TV", known.Name);
        Assert.Equal(ClientStore.Fingerprint(certificate), known.Fingerprint);
        Assert.Equal(1, clients.Count());
        Assert.Equal("Living-room TV", Assert.Single(clients.All()).Name);
    }

    [Fact]
    public void Pairing_again_replaces_the_earlier_record_of_the_same_device()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var clients = new ClientStore(database);

        using var first = ClientCertificate();
        using var second = ClientCertificate();

        // Every Moonlight sends the same identifier; the name is what tells devices apart.
        clients.Admit("0123456789ABCDEF", "Phone", first);
        clients.Admit("0123456789ABCDEF", "Phone", second);
        clients.Admit("0123456789ABCDEF", "Laptop", ClientCertificate());

        Assert.Null(clients.Find(first));
        Assert.NotNull(clients.Find(second));
        Assert.Equal(2, clients.Count());
    }

    [Fact]
    public void Forgetting_removes_only_that_device()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var clients = new ClientStore(database);

        using var keep = ClientCertificate();
        using var drop = ClientCertificate();
        clients.Admit("a", "Keep", keep);
        clients.Admit("b", "Drop", drop);

        var dropped = clients.All().Single(c => c.Name == "Drop");
        Assert.True(clients.Forget(dropped.Id));
        Assert.False(clients.Forget(dropped.Id));

        Assert.NotNull(clients.Find(keep));
        Assert.Null(clients.Find(drop));
    }

    [Fact]
    public void A_device_not_seen_for_half_a_year_is_forgotten_at_startup()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var clients = new ClientStore(database);

        using var old = ClientCertificate();
        using var recent = ClientCertificate();
        clients.Admit("a", "Old", old);
        clients.Admit("b", "Recent", recent);

        lock (database.Gate)
        {
            using var age = database.Command(
                "UPDATE clients SET last_seen_at = '2020-01-01 00:00:00' WHERE name = 'Old';");
            age.ExecuteNonQuery();
        }

        clients.ForgetStale();

        Assert.Null(clients.Find(old));
        Assert.NotNull(clients.Find(recent));
    }

    [Fact]
    public void Touching_keeps_a_device_from_being_forgotten()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var clients = new ClientStore(database);

        using var certificate = ClientCertificate();
        clients.Admit("a", "TV", certificate);

        lock (database.Gate)
        {
            using var age = database.Command("UPDATE clients SET last_seen_at = '2020-01-01 00:00:00';");
            age.ExecuteNonQuery();
        }

        clients.Touch(ClientStore.Fingerprint(certificate));
        clients.ForgetStale();

        Assert.NotNull(clients.Find(certificate));
    }
}
