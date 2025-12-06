using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Analyzers.Sample.Async;

// Should be treated as middleware and ignored for CT rules.
internal sealed class DeviceIdentificationMiddleware(RequestDelegate next)
{
   public async Task InvokeAsync(HttpContext context, IHostEnvironment environment, IConfiguration configuration)
   {
      await next(context);
   }
}