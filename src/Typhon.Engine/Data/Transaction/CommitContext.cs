// unset

using JetBrains.Annotations;

namespace Typhon.Engine;

[PublicAPI]
public delegate void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver);

internal ref struct CommitContext
{
    public long PrimaryKey;
    public ComponentInfoBase Info;
    public ref ComponentInfoBase.CompRevInfo CompRevInfo;
    public ConcurrencyConflictSolver Solver;
    public ConcurrencyConflictHandler Handler;
    public bool IsRollback;
    public ref UnitOfWorkContext Ctx;

    // Hoisted from per-entity to per-commit: determined once before the entity loop
    public bool IsTail;
    public long NextMinTSN;  // Valid when IsTail == true: the TSN to keep revisions for
    public long TailTSN;     // Valid when IsTail == false: the blocking tail's TSN
}
