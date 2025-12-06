using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pandatech.Analyzers.Async;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncMethodConventionsAnalyzer : DiagnosticAnalyzer
{
   public const string AsyncSuffixId = "PT0001";
   public const string CancellationTokenMissingId = "PT0002";
   public const string CancellationTokenNameId = "PT0003";
   public const string CancellationTokenPositionId = "PT0004";

   private static readonly DiagnosticDescriptor AsyncSuffixRule = new(
      AsyncSuffixId,
      "Async methods must end with 'Async'",
      "Async method '{0}' should be suffixed with 'Async'",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      isEnabledByDefault: true);

   private static readonly DiagnosticDescriptor CancellationTokenMissingRule = new(
      CancellationTokenMissingId,
      "Async methods must accept a CancellationToken",
      "Async method '{0}' should declare a CancellationToken parameter",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      isEnabledByDefault: true);

   private static readonly DiagnosticDescriptor CancellationTokenNameRule = new(
      CancellationTokenNameId,
      "CancellationToken parameter must be named 'ct'",
      "Async method '{0}' has CancellationToken parameter '{1}'; rename it to 'ct'",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      isEnabledByDefault: true);

   private static readonly DiagnosticDescriptor CancellationTokenPositionRule = new(
      CancellationTokenPositionId,
      "CancellationToken parameter must be last",
      "Async method '{0}' has CancellationToken parameter '{1}' that should be the last parameter",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      isEnabledByDefault: true);

   public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
      ImmutableArray.Create(
         AsyncSuffixRule,
         CancellationTokenMissingRule,
         CancellationTokenNameRule,
         CancellationTokenPositionRule
      );

   public override void Initialize(AnalysisContext context)
   {
      context.EnableConcurrentExecution();
      context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

      context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
      context.RegisterOperationAction(AnalyzeAnonymousFunction, OperationKind.AnonymousFunction);
   }

   private static void AnalyzeMethod(SymbolAnalysisContext context)
   {
      var method = (IMethodSymbol)context.Symbol;

      if (method.IsImplicitlyDeclared)
      {
         return;
      }

      if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction))
      {
         return;
      }

      if (method.ReturnType is not INamedTypeSymbol returnType)
      {
         return;
      }

      if (!IsTaskLike(returnType))
      {
         return;
      }

      AnalyzeAsyncMember(
         method,
         method.Locations[0],
         method.Name,
         context.ReportDiagnostic);
   }

   private static void AnalyzeAnonymousFunction(OperationAnalysisContext context)
   {
      var anon = (IAnonymousFunctionOperation)context.Operation;
      var symbol = anon.Symbol;

      if (symbol.ReturnType is not INamedTypeSymbol returnType)
      {
         return;
      }

      if (!IsTaskLike(returnType))
      {
         return;
      }

      const string displayName = "anonymous function";

      AnalyzeAsyncMember(
         symbol,
         anon.Syntax.GetLocation(),
         displayName,
         context.ReportDiagnostic);
   }

   private static void AnalyzeAsyncMember(
      IMethodSymbol method,
      Location location,
      string displayName,
      Action<Diagnostic> report)
   {
      // PT0001 – name must end with Async (for named methods).
      if (!displayName.Equals("anonymous function", StringComparison.Ordinal) &&
          !displayName.EndsWith("Async", StringComparison.Ordinal))
      {
         report(Diagnostic.Create(AsyncSuffixRule, location, displayName));
      }

      var ctInfo = GetCancellationTokenInfo(method);

      if (!ctInfo.HasCt)
      {
         report(Diagnostic.Create(CancellationTokenMissingRule, location, displayName));
         return;
      }

      if (!ctInfo.IsNamedCt)
      {
         report(Diagnostic.Create(CancellationTokenNameRule, location, displayName, ctInfo.Name));
      }

      if (!ctInfo.IsLast)
      {
         report(Diagnostic.Create(CancellationTokenPositionRule, location, displayName, ctInfo.Name));
      }
   }

   private static bool IsTaskLike(INamedTypeSymbol type)
   {
      if (type.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks")
      {
         return false;
      }

      return type.Name is "Task" or "ValueTask";
   }

   private readonly struct CtInfo
   {
      public bool HasCt { get; }
      public bool IsNamedCt { get; }
      public bool IsLast { get; }
      public string Name { get; }

      public CtInfo(bool hasCt, bool isNamedCt, bool isLast, string name)
      {
         HasCt = hasCt;
         IsNamedCt = isNamedCt;
         IsLast = isLast;
         Name = name;
      }
   }

   private static CtInfo GetCancellationTokenInfo(IMethodSymbol method)
   {
      IParameterSymbol? ctParam = null;
      var parameters = method.Parameters;

      for (var i = 0; i < parameters.Length; i++)
      {
         var p = parameters[i];

         if (p.Type is not INamedTypeSymbol named)
         {
            continue;
         }

         if (named.Name != "CancellationToken")
         {
            continue;
         }

         if (named.ContainingNamespace.ToDisplayString() != "System.Threading")
         {
            continue;
         }

         ctParam = p;
         break;
      }

      if (ctParam is null)
      {
         return new CtInfo(false, false, false, string.Empty);
      }

      var isNamedCt = string.Equals(ctParam.Name, "ct", StringComparison.Ordinal);
      var isLast = ctParam.Ordinal == parameters.Length - 1;

      return new CtInfo(true, isNamedCt, isLast, ctParam.Name);
   }
}
