// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Analyzers.Sample;

public interface IOrderService
{
   // ✅ OK: name, return type, ct last + named ct
   Task<Order> GetOrderAsync(int id, CancellationToken ct);

   // ❌ PT0001 (no Async suffix), ❌ PT0002 (no CT at all)
   Task<Order> GetOrder(int id);

   // ❌ PT0002 (CT is not last, name is wrong)
   Task<Order> GetOrderByCodeAsync(CancellationToken token, string code);
}

public sealed class OrderService : IOrderService
{
   public Task<Order> GetOrderAsync(int id, CancellationToken ct)
   {
      return Task.FromResult(new Order(id));
   }

   public Task<Order> GetOrder(int id)
   {
      return Task.FromResult(new Order(id));
      // PT0001 + PT0002
   }

   public Task<Order> GetOrderByCodeAsync(CancellationToken token, string code)
   {
      return Task.FromResult(new Order(42));
      // PT0002
   }
}

// Simulated controller-style class.
public sealed class OrdersController
{
   // ✅ OK
   public static Task<Order> GetOrderAsync(int id, CancellationToken ct)
   {
      return Task.FromResult(new Order(id));
   }

   // ❌ PT0001 (no Async suffix)
   public Task<Order> GetOrder(int id, CancellationToken ct)
   {
      return Task.FromResult(new Order(id));
   }

   // ❌ PT0002 (CT not last)
   public Task<Order> GetOrderDetailsAsync(CancellationToken ct, int id)
   {
      return Task.FromResult(new Order(id));
   }
}

// Minimal API style sample.
public static class OrderEndpoints
{
   public static void MapOrderEndpoints(WebApplication app)
   {
      // ✅ OK: ct last, Task<Order>
      app.MapGet("/orders/{id:int}",
         async (int id, CancellationToken ct) =>
         {
            await Task.Delay(10, ct);
            return Results.Ok(new Order(id));
         });

      // ❌ PT0002: missing CT parameter
      app.MapGet("/orders/no-ct/{id:int}",
         async (int id) =>
         {
            await Task.Delay(10);
            return Results.Ok(new Order(id));
         });

      // ❌ PT0002: CT name is wrong + not last
      app.MapGet("/orders/bad-ct/{id:int}",
         async (CancellationToken token, int id) =>
         {
            await Task.Delay(10, token);
            return Results.Ok(new Order(id));
         });
   }
}

public sealed class Order
{
   public Order(int id)
   {
      Id = id;
   }

   public int Id { get; }
}

public class Examples
{
}