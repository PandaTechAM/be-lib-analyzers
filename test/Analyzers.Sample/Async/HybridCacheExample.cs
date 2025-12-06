using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Hybrid;

namespace Analyzers.Sample.Async;

public class HybridCacheExample(HybridCache cacheService)
{
   public async Task FooAsync(CancellationToken ct = default)
   {
      var value = await cacheService.GetOrCreateAsync<Something>(
         "someKey",
         async innerCt => await GetFooAsync(innerCt),
         new HybridCacheEntryOptions
         {
            Expiration = TimeSpan.FromMinutes(1)
         },
         cancellationToken: ct);
   }

   public async Task<Something> GetFooAsync(CancellationToken ct = default)
   {
      ct.ThrowIfCancellationRequested();
      return new Something(2, "Example");
   }

   public record Something(int Id, string Name);
}