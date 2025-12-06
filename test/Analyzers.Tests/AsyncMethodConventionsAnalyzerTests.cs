using System.Threading.Tasks;
using Xunit;
using VerifyCS =
   Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
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
}
