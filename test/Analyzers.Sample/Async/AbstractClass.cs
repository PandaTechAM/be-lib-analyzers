using System;
using System.Threading;
using System.Threading.Tasks;

namespace Analyzers.Sample.Async;

public abstract class BaseRepository
{
   // ⚠️ PT0002: Missing CancellationToken
   // Code fix adds CT here ONLY - breaks the override below!
   public virtual Task<Entity> GetByIdAsync(int id)
   {
      throw new NotImplementedException();
   }
    
   // ⚠️ PT0004: CT not in last position
   // Code fix moves CT here ONLY - breaks the override below!
   public virtual Task<Entity> FindAsync(CancellationToken ct, string query)
   {
      throw new NotImplementedException();
   }
    
   // ⚠️ PT0003: CT should be named 'ct'
   // Renamer.RenameSymbolAsync SHOULD handle this correctly across overrides
   // (this one might actually work)
   public virtual Task SaveAsync(Entity entity, CancellationToken cancellationToken)
   {
      throw new NotImplementedException();
   }
}

public class UserRepository : BaseRepository
{
   // No warning (IsOverride = true, skipCtMissingAndPosition = true)
   // BUT: After applying fix to base class, this won't compile!
   public override Task<Entity> GetByIdAsync(int id)
   {
      return Task.FromResult<Entity>(new User());
   }
    
   // No warning (IsOverride = true)
   // BUT: After applying fix to base class, this won't compile!
   public override Task<Entity> FindAsync(CancellationToken ct, string query)
   {
      return Task.FromResult<Entity>(new User());
   }
    
   public override Task SaveAsync(Entity entity, CancellationToken cancellationToken)
   {
      return Task.CompletedTask;
   }
}

public class Entity { }
public class User : Entity { }