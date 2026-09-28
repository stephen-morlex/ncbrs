using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCBRS.Client.Sync;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Sealed USB transfer files (WS-H2): births carried on a stick from a post
/// with no network, readable by the registry alone. The signed envelope gave
/// integrity; it gave no confidentiality — names and dates of birth sat in it
/// as base64. These pin the seal, the registry's key, and the endpoint that
/// opens a sealed file and treats what is inside exactly as a sync.
/// </summary>
public class SealedTransferTests
{
    private static readonly byte[] Births = Encoding.UTF8.GetBytes("""{"childFullName":"Nyandeng Garang","motherFullName":"Achol Deng"}""");

    private static ECDiffieHellman Key(string privatePem)
    {
        var key = ECDiffieHellman.Create();
        key.ImportFromPem(privatePem);
        return key;
    }

    [Fact]
    public void OnlyTheRegistrysKeyOpensTheFile()
    {
        var (privatePem, publicPem) = SealedTransfer.GenerateKeyPair();
        var sealedFile = SealedTransfer.Seal(Births, publicPem, "moh-transfer-2026");

        using var key = Key(privatePem);
        var opened = SealedTransfer.Open(sealedFile, id => id == "moh-transfer-2026" ? key : null);

        Assert.True(opened.Opened);
        Assert.Equal(Births, opened.Plaintext);
        Assert.Equal("moh-transfer-2026", opened.KeyId);
    }

    /// <summary>The point of it: nothing a carrier holds names anyone.</summary>
    [Fact]
    public void TheFileNamesNobody()
    {
        var (_, publicPem) = SealedTransfer.GenerateKeyPair();

        var onDisk = Encoding.UTF8.GetString(SealedTransfer.Seal(Births, publicPem, "moh-transfer-2026"));

        Assert.DoesNotContain("Nyandeng", onDisk);
        Assert.DoesNotContain(Convert.ToBase64String(Births)[..20], onDisk);
    }

    [Fact]
    public void AnotherKeyCannotOpenIt()
    {
        var (_, publicPem) = SealedTransfer.GenerateKeyPair();
        var (otherPrivate, _) = SealedTransfer.GenerateKeyPair();
        var sealedFile = SealedTransfer.Seal(Births, publicPem, "moh-transfer-2026");

        using var wrong = Key(otherPrivate);
        var opened = SealedTransfer.Open(sealedFile, _ => wrong);

        Assert.False(opened.Opened);
        Assert.Null(opened.Plaintext);
    }

    [Fact]
    public void AFileSealedToAKeyTheRegistryDoesNotHoldSaysWhichKey()
    {
        var (_, publicPem) = SealedTransfer.GenerateKeyPair();

        var opened = SealedTransfer.Open(SealedTransfer.Seal(Births, publicPem, "moh-transfer-2024"), _ => null);

        Assert.False(opened.Opened);
        Assert.Contains("moh-transfer-2024", opened.Reason);
    }

    /// <summary>Every field is bound in: changing any one of them fails the tag.</summary>
    [Theory]
    [InlineData("ciphertext")]
    [InlineData("nonce")]
    [InlineData("tag")]
    [InlineData("keyId")]
    [InlineData("ephemeralKey")]
    public void AnAlteredFileIsRefused(string field)
    {
        var (privatePem, publicPem) = SealedTransfer.GenerateKeyPair();
        var (_, otherPublic) = SealedTransfer.GenerateKeyPair();
        var file = JsonSerializer.Deserialize<SealedTransferFile>(
            SealedTransfer.Seal(Births, publicPem, "moh-transfer-2026"), JsonSerializerOptions.Web)!;
        static string Flip(string base64)
        {
            var bytes = Convert.FromBase64String(base64);
            bytes[^1] ^= 0x01;
            return Convert.ToBase64String(bytes);
        }

        var altered = field switch
        {
            "ciphertext" => file with { Ciphertext = Flip(file.Ciphertext) },
            "nonce" => file with { Nonce = Flip(file.Nonce) },
            "tag" => file with { Tag = Flip(file.Tag) },
            "keyId" => file with { KeyId = "moh-transfer-2026b" },
            _ => file with { EphemeralKey = Convert.ToBase64String(Convert.FromBase64String(
                     otherPublic.Replace("-----BEGIN PUBLIC KEY-----", "").Replace("-----END PUBLIC KEY-----", "").Trim())) },
        };

        using var key = Key(privatePem);
        Assert.False(SealedTransfer.Open(altered, _ => key).Opened);
    }

    /// <summary>A fresh ephemeral key per file: two files never share a key.</summary>
    [Fact]
    public void NoTwoFilesShareAKey()
    {
        var (_, publicPem) = SealedTransfer.GenerateKeyPair();
        var first = JsonSerializer.Deserialize<SealedTransferFile>(SealedTransfer.Seal(Births, publicPem, "k"), JsonSerializerOptions.Web)!;
        var second = JsonSerializer.Deserialize<SealedTransferFile>(SealedTransfer.Seal(Births, publicPem, "k"), JsonSerializerOptions.Web)!;

        Assert.NotEqual(first.EphemeralKey, second.EphemeralKey);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    // --- the registry's key ---------------------------------------------------------------------

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "NCBRS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    internal static TransferKeyring DevelopmentKeyring()
        => new(Options.Create(new TransferEncryptionOptions { AllowEphemeralDevelopmentKey = true }),
            new Environment(Environments.Development), NullLogger<TransferKeyring>.Instance);

    [Fact]
    public void OutsideDevelopmentAMissingKeyRefusesToStart()
        => Assert.Throws<InvalidOperationException>(() => new TransferKeyring(
            Options.Create(new TransferEncryptionOptions { KeyId = "moh-transfer-2026", AllowEphemeralDevelopmentKey = true }),
            new Environment(Environments.Production), NullLogger<TransferKeyring>.Instance));

    [Fact]
    public void OutsideDevelopmentTheDevelopmentKeyIdRefusesToStart()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, SealedTransfer.GenerateKeyPair().PrivateKeyPem);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new TransferKeyring(
                Options.Create(new TransferEncryptionOptions { PrivateKeyPath = path }),
                new Environment(Environments.Production), NullLogger<TransferKeyring>.Instance));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A stick may take weeks: after a rotation, a file sealed to the outgoing key still opens.</summary>
    [Fact]
    public void AFileSealedBeforeARotationStillOpens()
    {
        var (oldPrivate, oldPublic) = SealedTransfer.GenerateKeyPair();
        var (newPrivate, _) = SealedTransfer.GenerateKeyPair();
        var oldPath = Path.GetTempFileName();
        var newPath = Path.GetTempFileName();
        File.WriteAllText(oldPath, oldPrivate);
        File.WriteAllText(newPath, newPrivate);
        try
        {
            using var keyring = new TransferKeyring(
                Options.Create(new TransferEncryptionOptions
                {
                    KeyId = "moh-transfer-2027",
                    PrivateKeyPath = newPath,
                    RetiredKeys = [new RetiredTransferKey { KeyId = "moh-transfer-2026", PrivateKeyPath = oldPath }],
                }),
                new Environment(Environments.Production), NullLogger<TransferKeyring>.Instance);

            Assert.True(keyring.Open(SealedTransfer.Seal(Births, oldPublic, "moh-transfer-2026")).Opened);
            Assert.Equal("moh-transfer-2027", keyring.KeyId);
        }
        finally
        {
            File.Delete(oldPath);
            File.Delete(newPath);
        }
    }

    /// <summary>What the tablet seals (client core), the registry opens (Api), and the device's signature still verifies.</summary>
    [Fact]
    public void WhatTheTabletSealsTheRegistryOpensAndTheSignatureHolds()
    {
        using var keyring = DevelopmentKeyring();
        var signer = DeviceSigner.Generate();

        var file = OfflineTransferFile.PackSealed("TAB-0A1B2C3D4E5F", Births, signer, keyring.PublicKeyPem, keyring.KeyId);
        var opened = keyring.Open(file);
        var envelope = TransferEnvelopes.Read(opened.Plaintext);

        Assert.True(opened.Opened);
        Assert.Equal("TAB-0A1B2C3D4E5F", envelope.DeviceId);
        Assert.Equal(Births, envelope.Body);
        Assert.True(DeviceSignature.Verify(signer.PublicKeyPem, envelope.Body!, envelope.Signature!).Valid);
    }
}

/// <summary>
/// The endpoint a sync point uploads a sealed file to. Once opened, what is
/// inside is a sync: the device that signed it is checked as any uploading
/// device is, and the births are processed and answered as in any batch.
/// </summary>
public sealed class SealedTransferEndpointTests : IDisposable
{
    private static readonly Guid FacilityId = Guid.Parse("0199a1b2-0001-7000-8000-000000000001");
    private static readonly Guid RegistrarId = Guid.Parse("0199a1b2-1001-7000-8000-000000000001");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _database = TestDatabase.Create();
    private readonly TransferKeyring _keyring = SealedTransferTests.DevelopmentKeyring();

    public SealedTransferEndpointTests()
    {
        using var db = new NcbrsDbContext(_database.Options);
        db.Facilities.Add(new Facility
        {
            FacilityId = FacilityId, Name = "Terekeka Village Health Post", CountyCode = "SS-CE-TER",
            BrnBlockStart = 100_000, BrnBlockEnd = 199_999, BrnBlockNextAvailable = 100_100,
        });
        db.Devices.Add(new Device
        {
            DeviceId = "TABLET-07", FacilityId = FacilityId, PublicKeyPem = DeviceTestKeys.PublicKeyPem, EnrolledByRegistrarId = RegistrarId,
        });
        db.Registrars.Add(new Registrar
        {
            RegistrarId = RegistrarId, FacilityId = FacilityId, ExternalSubjectId = AuthTestContext.DefaultSubject,
            DisplayName = "Nurse A. Lado", CredentialHash = "dev-placeholder",
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _keyring.Dispose();
        _database.Dispose();
    }

    private static byte[] BatchBody(string deviceId = "TABLET-07", params string[] brns)
        => JsonSerializer.SerializeToUtf8Bytes(new ApiRequest<SyncBatchRequest>
        {
            Meta = new RequestMeta { TransactionId = Guid.CreateVersion7() },
            Data = new SyncBatchRequest
            {
                DeviceId = deviceId,
                FacilityId = FacilityId,
                Records = [.. (brns.Length == 0 ? ["100001"] : brns).Select(brn => new SyncBirthRecord
                {
                    Birth = new RegisterBirthRequest
                    {
                        Brn = brn, FacilityId = FacilityId, DeviceId = deviceId, ChildFullName = "Nyandeng Garang",
                        DateOfBirth = DateTime.UtcNow.Date.AddDays(-5), Sex = Sex.Female, RegisteredAtUtc = DateTime.UtcNow.AddDays(-4),
                    },
                })],
            },
        }, Json);

    private ApiRequest<SealedTransferFile> Sealed(byte[] body, string signature, string envelopeDevice = "TABLET-07")
        => new()
        {
            Data = JsonSerializer.Deserialize<SealedTransferFile>(
                SealedTransfer.Seal(TransferEnvelopes.Pack(envelopeDevice, body, signature), _keyring.PublicKeyPem, _keyring.KeyId),
                JsonSerializerOptions.Web)!,
        };

    private async Task<ActionResult<SyncBatchResponse>> UploadAsync(ApiRequest<SealedTransferFile> upload)
    {
        await using var db = new NcbrsDbContext(_database.Options);
        var http = AuthTestContext.HttpContextFor();
        var current = AuthTestContext.RegistrarService(db, http);
        var publisher = new NoOpEventPublisher();
        var controller = new SyncController(
            db,
            new BirthRegistrationService(db, publisher, current,
                new DuplicateDetectionService(db, new DuplicateMatcher(), new CertificateRevocationRecorder(db), NullLogger<DuplicateDetectionService>.Instance, new CountyLookup(db)),
                new CountyLookup(db), Options.Create(new StatutoryRegistrationOptions())),
            current, publisher, new ProvisionalRecordReconciler(db, new CountyLookup(db)),
            // Signatures required, as in production: the device's signature
            // over the batch inside is the proof a sealed file carries.
            new DeviceEnrolmentService(db, new DeviceEnrolmentOptions { RequireSignature = true }),
            new RegisterBirthRequestValidator(), new CountyLookup(db), new RefusalAudit(db, NullLogger<RefusalAudit>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var json = Options.Create(new JsonOptions());
        json.Value.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        return await controller.SubmitTransfer(upload, _keyring, json);
    }

    private static string FieldOf(ActionResult<SyncBatchResponse> result)
        => Assert.IsType<ObjectResult>(result.Result).Value is ApiErrorResponse error ? error.Errors[0].Field : "";

    [Fact]
    public async Task ASealedFileFromAnEnrolledDeviceRegistersItsBirths()
    {
        var body = BatchBody(brns: ["100001", "100002"]);

        var result = await UploadAsync(Sealed(body, DeviceTestKeys.Sign(body)));

        var response = Assert.IsType<SyncBatchResponse>(result.Value);
        Assert.Equal(2, response.Registered);
        await using var db = new NcbrsDbContext(_database.Options);
        Assert.Equal(2, await db.BirthRecords.CountAsync());
        Assert.Contains(await db.AuditLogs.Select(log => log.Action).ToListAsync(),
            action => action == $"SyncBatchProcessed:SealedTransfer:{_keyring.KeyId}");
    }

    [Fact]
    public async Task AFileSignedByAnotherDeviceIsRefusedAsTheDevice()
    {
        var body = BatchBody();

        var result = await UploadAsync(Sealed(body, DeviceTestKeys.SignWithOtherKey(Encoding.UTF8.GetString(body))));

        Assert.Equal(403, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal("data.deviceId", FieldOf(result));
    }

    [Fact]
    public async Task AnAlteredSealedFileIsRefusedUnopened()
    {
        var body = BatchBody();
        var upload = Sealed(body, DeviceTestKeys.Sign(body));
        var bytes = Convert.FromBase64String(upload.Data.Ciphertext);
        bytes[0] ^= 0x01;

        var result = await UploadAsync(new ApiRequest<SealedTransferFile> { Data = upload.Data with { Ciphertext = Convert.ToBase64String(bytes) } });

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal("file", FieldOf(result));
        await using var db = new NcbrsDbContext(_database.Options);
        Assert.Equal(0, await db.BirthRecords.CountAsync());
    }

    /// <summary>One device's signature carried on another's batch is refused before anything else.</summary>
    [Fact]
    public async Task AnEnvelopeNamingAnotherDeviceThanItsBatchIsRefused()
    {
        var body = BatchBody();

        var result = await UploadAsync(Sealed(body, DeviceTestKeys.Sign(body), envelopeDevice: "TABLET-99"));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task AFileSealedToAnotherRegistryIsRefused()
    {
        var body = BatchBody();
        var (_, otherPublic) = SealedTransfer.GenerateKeyPair();
        var foreign = JsonSerializer.Deserialize<SealedTransferFile>(
            SealedTransfer.Seal(TransferEnvelopes.Pack("TABLET-07", body, DeviceTestKeys.Sign(body)), otherPublic, _keyring.KeyId),
            JsonSerializerOptions.Web)!;

        var result = await UploadAsync(new ApiRequest<SealedTransferFile> { Data = foreign });

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    /// <summary>Carried twice — two sticks, or a stick and a later sync — the births are held once.</summary>
    [Fact]
    public async Task TheSameFileUploadedTwiceRegistersTheBirthsOnce()
    {
        var body = BatchBody(brns: ["100003"]);
        var upload = Sealed(body, DeviceTestKeys.Sign(body));

        await UploadAsync(upload);
        var second = Assert.IsType<SyncBatchResponse>((await UploadAsync(upload)).Value);

        Assert.Equal(1, second.Duplicates);
        await using var db = new NcbrsDbContext(_database.Options);
        Assert.Equal(1, await db.BirthRecords.CountAsync());
    }
}
