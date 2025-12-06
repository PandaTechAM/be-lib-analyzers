using System;
using System.Collections.Immutable;
using System.Linq;
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
      true);

   private static readonly DiagnosticDescriptor CancellationTokenMissingRule = new(
      CancellationTokenMissingId,
      "Async methods must accept a CancellationToken",
      "Async method '{0}' should declare a CancellationToken parameter",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      true);

   private static readonly DiagnosticDescriptor CancellationTokenNameRule = new(
      CancellationTokenNameId,
      "CancellationToken parameter must be named 'ct'",
      "Async method '{0}' has CancellationToken parameter '{1}'; rename it to 'ct'",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      true);

   private static readonly DiagnosticDescriptor CancellationTokenPositionRule = new(
      CancellationTokenPositionId,
      "CancellationToken parameter must be last",
      "Async method '{0}' has CancellationToken parameter '{1}' that should be the last parameter",
      "AsyncUsage",
      DiagnosticSeverity.Warning,
      true);

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
      context.RegisterOperationAction(AnalyzeMinimalApiInvocation, OperationKind.Invocation);
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

      var hasSourceLocation = false;
      foreach (var location in method.Locations)
      {
         if (!location.IsInSource)
         {
            continue;
         }

         hasSourceLocation = true;
         break;
      }

      if (!hasSourceLocation)
      {
         return;
      }

      if (!method.IsAsync)
      {
         if (method.ReturnType is not INamedTypeSymbol returnType || !returnType.IsTaskLike())
         {
            return;
         }
      }

      AnalyzeAsyncMember(
         method,
         method.Locations[0],
         method.Name,
         context.ReportDiagnostic);
   }

   private static void AnalyzeAsyncMember(IMethodSymbol method,
      Location location,
      string displayName,
      Action<Diagnostic> report)
   {
      var isContract = method.IsContractImplementation();
      var isTest = method.IsTestMethod();
      var isMiddleware = method.IsMiddleware();

      var skipCtMissingAndPosition = isContract || isTest || isMiddleware;

      if (!displayName.Equals("anonymous function", StringComparison.Ordinal) &&
          !displayName.EndsWith("Async", StringComparison.Ordinal) &&
          !isContract)
      {
         report(Diagnostic.Create(AsyncSuffixRule, location, displayName));
      }

      var ctInfo = GetCancellationTokenInfo(method);

      if (!ctInfo.HasCt)
      {
         if (!skipCtMissingAndPosition)
         {
            report(Diagnostic.Create(CancellationTokenMissingRule, location, displayName));
         }

         return;
      }

      if (!ctInfo.IsNamedCt && !isContract && !isMiddleware)
      {
         report(Diagnostic.Create(CancellationTokenNameRule, location, displayName, ctInfo.Name));
      }

      if (!ctInfo.IsLast && !skipCtMissingAndPosition)
      {
         report(Diagnostic.Create(CancellationTokenPositionRule, location, displayName, ctInfo.Name));
      }
   }

   private static void AnalyzeMinimalApiInvocation(OperationAnalysisContext context)
   {
      var invocation = (IInvocationOperation)context.Operation;
      var target = invocation.TargetMethod;

      if (!target.IsMinimalApiMapMethod())
      {
         return;
      }

      IAnonymousFunctionOperation? handlerAnon = null;

      foreach (var arg in invocation.Arguments)
      {
         handlerAnon = AsyncHelpers.ExtractAnonymousFunction(arg.Value);
         if (handlerAnon is not null)
         {
            break;
         }
      }

      if (handlerAnon is null)
      {
         return;
      }

      var handlerSymbol = handlerAnon.Symbol;

      if (handlerSymbol.ReturnType is not INamedTypeSymbol returnType ||
          !returnType.IsTaskLike())
      {
         return;
      }

      var ctInfo = GetCancellationTokenInfo(handlerSymbol);
      const string displayName = "anonymous function";
      var location = handlerAnon.Syntax.GetLocation();

      if (!ctInfo.HasCt)
      {
         context.ReportDiagnostic(
            Diagnostic.Create(
               CancellationTokenMissingRule,
               location,
               displayName));

         return;
      }

      if (!ctInfo.IsNamedCt)
      {
         context.ReportDiagnostic(
            Diagnostic.Create(
               CancellationTokenNameRule,
               location,
               displayName,
               ctInfo.Name));
      }

      if (!ctInfo.IsLast)
      {
         context.ReportDiagnostic(
            Diagnostic.Create(
               CancellationTokenPositionRule,
               location,
               displayName,
               ctInfo.Name));
      }
   }

   private static CtInfo GetCancellationTokenInfo(IMethodSymbol method)
   {
      var parameters = method.Parameters;

      var ctParam = Enumerable.FirstOrDefault(parameters, p => p.Type.IsCancellationToken());

      if (ctParam is null)
      {
         return new CtInfo(false, false, false, string.Empty);
      }

      var isNamedCt = string.Equals(ctParam.Name, "ct", StringComparison.Ordinal);

      var lastNonParamsIndex = -1;
      for (var i = 0; i < parameters.Length; i++)
      {
         if (!parameters[i].IsParams)
         {
            lastNonParamsIndex = i;
         }
      }

      var isLast = ctParam.Ordinal == lastNonParamsIndex;

      return new CtInfo(true, isNamedCt, isLast, ctParam.Name);
   }

   private readonly struct CtInfo(bool hasCt, bool isNamedCt, bool isLast, string name)
   {
      public bool HasCt { get; } = hasCt;
      public bool IsNamedCt { get; } = isNamedCt;
      public bool IsLast { get; } = isLast;
      public string Name { get; } = name;
   }
}