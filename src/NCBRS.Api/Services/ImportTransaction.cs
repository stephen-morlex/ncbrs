using Microsoft.EntityFrameworkCore.Storage;
using NCBRS.Data;

namespace NCBRS.Services;

/// <summary>
/// All or nothing for a file import: its own transaction, or, when the request
/// already runs inside one (the idempotency filter opens one for a
/// caller-supplied transaction id), a savepoint in that. Either way
/// <see cref="UndoAsync"/> takes back every row of the file and nothing else.
/// </summary>
public sealed class ImportTransaction : IAsyncDisposable
{
    private readonly NcbrsDbContext _db;
    private readonly IDbContextTransaction? _own;
    private readonly IDbContextTransaction? _ambient;
    private readonly string _savepoint = $"import_{Guid.NewGuid():N}";

    private ImportTransaction(NcbrsDbContext db, IDbContextTransaction? own, IDbContextTransaction? ambient)
    {
        _db = db;
        _own = own;
        _ambient = ambient;
    }

    public static async Task<ImportTransaction> BeginAsync(NcbrsDbContext db, CancellationToken cancellationToken)
    {
        var ambient = db.Database.CurrentTransaction;
        if (ambient is not null)
        {
            var import = new ImportTransaction(db, null, ambient);
            await ambient.CreateSavepointAsync(import._savepoint, cancellationToken);
            return import;
        }

        return new ImportTransaction(db, await db.Database.BeginTransactionAsync(cancellationToken), null);
    }

    public async Task UndoAsync(CancellationToken cancellationToken)
    {
        if (_own is not null)
        {
            await _own.RollbackAsync(cancellationToken);
        }
        else
        {
            await _ambient!.RollbackToSavepointAsync(_savepoint, cancellationToken);
        }

        _db.ChangeTracker.Clear();
    }

    /// <summary>Commits our own transaction. Inside the caller's, it commits with theirs.</summary>
    public Task CommitAsync(CancellationToken cancellationToken)
        => _own?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _own?.DisposeAsync() ?? ValueTask.CompletedTask;
}
