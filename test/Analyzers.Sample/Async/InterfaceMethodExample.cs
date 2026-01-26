// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Analyzers.Sample.Async;

public interface IOrderService
{
   // OK: name, CT last, CT named ct
   Task<Order> GetOrderAsync(int id, CancellationToken ct);

   // PT0001 + PT0002: no Async suffix, no CancellationToken
   Task<Order> GetOrder(int id);

   // PT0003 + PT0004: CT name wrong and not last
   Task<Order> GetOrderByCodeAsync(CancellationToken token, string code);

   // OK: CT named ct, last non-params, default allowed
   Task<Order> GetSomethingAsync(CancellationToken ct = default, params string[] args);

   // PT0002: no CT at all (only optional + params)
   Task<Order> GetMeAsync(int id = 5, CancellationToken ct = default, params object[] args);
}

public sealed class OrderService : IOrderService
{
   public Task<Order> GetOrderAsync(int id, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      return Task.FromResult(new Order(id));
   }

   public Task<Order> GetOrder(int id)
   {
      // PT0001 + PT0002
      return Task.FromResult(new Order(id));
   }

   public Task<Order> GetOrderByCodeAsync(CancellationToken token, string code)
   {
      // PT0003 + PT0004
      return Task.FromResult(new Order(42));
   }

   public Task<Order> GetSomethingAsync(CancellationToken ct = default, params string[] args)
   {
      ct.ThrowIfCancellationRequested();
      throw new NotImplementedException();
   }

   public Task<Order> GetMeAsync(int id = 5, CancellationToken ct = default, params object[] args)
   {
      throw new NotImplementedException();
   }
}

// Simulated controller-style class (no interface / contracts)
public sealed class OrdersController
{
   // OK
   public static Task<Order> GetOrderAsync(int id, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      return Task.FromResult(new Order(id));
   }

   // PT0001: no Async suffix
   public Task<Order> GetOrder(int id, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      return Task.FromResult(new Order(id));
   }

   // PT0004: CT not last
   public Task<Order> GetOrderDetailsAsync(CancellationToken ct, int id)
   {
      ct.ThrowIfCancellationRequested();
      return Task.FromResult(new Order(id));
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