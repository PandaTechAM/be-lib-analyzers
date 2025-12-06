using System.Threading.Tasks;
using Xunit;
using VerifyCS = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
   Pandatech.Analyzers.Async.AsyncMethodConventionsAnalyzer,
   Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Analyzers.Tests;

public class AsyncMethodConventionsAnalyzerTests
{
   [Fact]
   public async Task Valid_async_method_is_ok()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public interface IService
                          {
                              Task<int> GetValueAsync(CancellationToken ct);
                          }

                          public class Service : IService
                          {
                              public Task<int> GetValueAsync(CancellationToken ct) => Task.FromResult(42);
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Missing_async_suffix_reports_PT0001()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public Task<int> {|PT0001:GetValue|}(CancellationToken ct) => Task.FromResult(42);
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Missing_cancellation_token_reports_PT0002()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public Task<int> {|PT0002:GetValueAsync|}() => Task.FromResult(42);
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Wrong_ct_name_reports_PT0003()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public Task<int> {|PT0003:GetValueAsync|}(CancellationToken token) => Task.FromResult(42);
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Ct_not_last_reports_PT0004()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public Task<int> {|PT0004:GetValueAsync|}(CancellationToken ct, int id) => Task.FromResult(42);
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Missing_ct_on_interface_and_implementation_reports_only_on_interface()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public interface IService
                          {
                              Task {|PT0002:GetValueAsync|}();
                          }

                          public class Service : IService
                          {
                              public Task GetValueAsync() => Task.CompletedTask; // no PT0002 here
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Ct_name_rule_applies_on_contract_implementation()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public interface IService
                          {
                              Task GetValueAsync(CancellationToken ct);
                          }

                          public class Service : IService
                          {
                              public Task {|PT0003:GetValueAsync|}(CancellationToken token) => Task.CompletedTask;
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Ct_before_params_is_considered_last_non_params()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public Task GetValueAsync(CancellationToken ct, params string[] values)
                                  => Task.CompletedTask;
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Ct_before_other_non_params_reports_PT0004_even_with_params()
   {
      const string code = """
                          using System.Threading;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public Task {|PT0004:GetValueAsync|}(CancellationToken ct, int id, params string[] values)
                                  => Task.CompletedTask;
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task Anonymous_lambda_without_ct_is_ignored_when_delegate_has_no_ct()
   {
      const string code = """
                          using System;
                          using System.Threading.Tasks;

                          public class Service
                          {
                              public void Register()
                              {
                                  Func<Task> handler = async () =>
                                  {
                                      await Task.Delay(10);
                                  };
                              }
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }


   [Fact]
   public async Task Hangfire_expression_lambda_is_ignored()
   {
      const string code = """
                          using System;
                          using System.Threading;
                          using System.Threading.Tasks;

                          public interface IUserManagementIntegrationService
                          {
                              Task UpdateAuthenticationHistoryMissingLocationsAsync(CancellationToken ct);
                          }

                          public static class Jobs
                          {
                              public static void Register()
                              {
                                  RecurringJob.AddOrUpdate<IUserManagementIntegrationService>(
                                      "Update Authentication History Missing Locations",
                                      service => service.UpdateAuthenticationHistoryMissingLocationsAsync(CancellationToken.None),
                                      "0 * * * *",
                                      TimeZoneInfo.Utc);
                              }
                          }

                          public static class RecurringJob
                          {
                              public static void AddOrUpdate<T>(string id, System.Linq.Expressions.Expression<Func<T, Task>> method, string cron, TimeZoneInfo tz) { }
                          }
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }

   [Fact]
   public async Task RequestDelegate_lambda_is_ignored()
   {
      const string code = """
                          using System.Threading.Tasks;

                          public static class Extensions
                          {
                              public static void Configure(IEndpointConventionBuilder builder)
                              {
                                  builder.Add(endpointBuilder =>
                                  {
                                      var original = endpointBuilder.RequestDelegate;
                                      endpointBuilder.RequestDelegate = async context =>
                                      {
                                          await original!(context);
                                      };
                                  });
                              }
                          }

                          public interface IEndpointConventionBuilder
                          {
                              void Add(System.Action<EndpointBuilder> convention);
                          }

                          public class EndpointBuilder
                          {
                              public RequestDelegate? RequestDelegate { get; set; }
                          }

                          // Fake ASP.NET-like types just for the test
                          public class HttpContext { }

                          public delegate Task RequestDelegate(HttpContext context);
                          """;

      await VerifyCS.VerifyAnalyzerAsync(code);
   }
}