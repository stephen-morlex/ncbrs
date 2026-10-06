using NCBRS.Client.Storage;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// A registration saved half-way: kept in the encrypted store with the rest of
/// the tablet's state, so a family that has to leave, or a battery that dies,
/// costs nothing already typed.
/// </summary>
public sealed class FormDraftTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ncbrs-drafts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The request's own Sex field cannot say "not yet" (its default is Male),
    /// so the draft carries whether one was chosen. Lost, a restored draft
    /// would register every unanswered child as a boy.
    /// </summary>
    [Fact]
    public async Task ADraftComesBackAsItWasLeftWithAnUnansweredSexStillUnanswered()
    {
        var store = new EncryptedStateFile(Path.Combine(_directory, "device.state"), EncryptedStateFile.NewKey());
        var id = Guid.NewGuid();
        var draft = new FormDraft(
            id,
            Step: 2,
            UpdatedAtUtc: new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc),
            Birth: new RegisterBirthRequest
            {
                ChildGivenNames = "Ayen",
                ChildSurname = "Garang",
                DateOfBirth = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
                PlaceOfBirthKind = PlaceOfBirthKind.Home,
                PlaceOfBirth = "Gudele",
                Mother = new ParentDetails { GivenNames = "Mary", Surname = "Akol" },
            },
            SexChosen: false,
            WithStatistics: true);

        await store.SaveAsync(new DeviceState { Drafts = [draft] });
        var restored = Assert.Single((await store.LoadAsync())!.Drafts);

        Assert.Equal(id, restored.Id);
        Assert.Equal(2, restored.Step);
        Assert.False(restored.SexChosen);
        Assert.True(restored.WithStatistics);
        Assert.Equal("Ayen", restored.Birth.ChildGivenNames);
        Assert.Equal(PlaceOfBirthKind.Home, restored.Birth.PlaceOfBirthKind);
        Assert.Equal("Gudele", restored.Birth.PlaceOfBirth);
        Assert.Equal("Akol", restored.Birth.Mother?.Surname);
    }

    /// <summary>A state saved before drafts existed loads with none, not as unreadable.</summary>
    [Fact]
    public async Task AStateFromBeforeDraftsLoadsWithNone()
    {
        var store = new EncryptedStateFile(Path.Combine(_directory, "device.state"), EncryptedStateFile.NewKey());
        await store.SaveAsync(new DeviceState());

        Assert.Empty((await store.LoadAsync())!.Drafts);
    }
}
