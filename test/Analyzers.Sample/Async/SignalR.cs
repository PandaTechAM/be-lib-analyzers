namespace Analyzers.Sample.Async;

using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

// Server-to-client interface - client implements these handlers
// These methods are called via SignalR from server to client
public interface IChatClient
{
   // ⚠️ PT0001: Missing "Async" suffix
   // ⚠️ PT0002: Missing CancellationToken
   // BUT: Changing this breaks client-side: connection.on("ReceiveMessage", ...)
   Task ReceiveMessage(string user, string message);
}

// Client-to-server hub interface
public interface IChatHub
{
   // ⚠️ PT0001: Missing "Async" suffix  
   // ⚠️ PT0002: Missing CancellationToken
   // BUT: Changing this breaks client-side: connection.invoke("SendMessage", ...)
   Task SendMessage(string user, string message);
}

// Hub implementation
public class ChatHub : Hub<IChatClient>, IChatHub
{
   // Implementation would also be flagged, but even if it weren't...
   // the INTERFACE itself gets warnings and code fixes would break clients
   public Task SendMessage(string user, string message)
   {
      return Clients.All.ReceiveMessage(user, message);
   }
}

// Non-interface hub methods are also contracts
public class NotificationHub : Hub
{
   // ⚠️ PT0001 + PT0002
   // Client calls: connection.invoke("Subscribe", topic)
   // Renaming to SubscribeAsync or adding CT breaks clients
   public Task Subscribe(string topic)
   {
      return Groups.AddToGroupAsync(Context.ConnectionId, topic);
   }
}