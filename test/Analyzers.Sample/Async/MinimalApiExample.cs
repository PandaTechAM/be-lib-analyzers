using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Analyzers.Sample.Async;

public static class OrderEndpoints
{
   public static void MapOrderEndpoints(WebApplication app)
   {
      // OK: ct last, Task<Order>
      app.MapGet("/orders/{id:int}",
         async (int id, CancellationToken ct) =>
         {
            await Task.Delay(10, ct);
            return Results.Ok(new Order(id));
         });

      // PT0002: missing CT parameter
      app.MapGet("/orders/no-ct/{id:int}",
         async (int id) =>
         {
            await Task.Delay(10);
            return Results.Ok(new Order(id));
         });

      // PT0003 + PT0004: CT name is wrong and not last
      app.MapGet("/orders/bad-ct/{id:int}",
         async (CancellationToken token, int id) =>
         {
            await Task.Delay(10, token);
            return Results.Ok(new Order(id));
         });
   }
}