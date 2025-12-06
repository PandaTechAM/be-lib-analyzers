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
      context.RegisterOperationAction(AnalyzeAnonymousFunction, OperationKind.AnonymousFunction);

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

      // Only analyze methods declared in source (skip metadata / external assemblies).
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

      // Only Task / ValueTask
      if (symbol.ReturnType is not INamedTypeSymbol returnType)
      {
         return;
      }

      if (!IsTaskLike(returnType))
      {
         return;
      }

      // For lambdas/anonymous functions we do NOT enforce "must have CT" (PT0002),
      // because the delegate signature is usually dictated by an external API
      // (Hangfire, ASP.NET, minimal APIs binder, etc.).
      // We only normalize name and position if a CT parameter already exists.
      var ctInfo = GetCancellationTokenInfo(symbol);
      if (!ctInfo.HasCt)
      {
         return;
      }

      const string displayName = "anonymous function";
      var location = anon.Syntax.GetLocation();

      // PT0003: name must be ct
      if (!ctInfo.IsNamedCt)
      {
         context.ReportDiagnostic(
            Diagnostic.Create(
               CancellationTokenNameRule,
               location,
               displayName,
               ctInfo.Name));
      }

      // PT0004: CT must be last non-params parameter
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


   private static void AnalyzeAsyncMember(IMethodSymbol method,
      Location location,
      string displayName,
      Action<Diagnostic> report)
   {
      var isContract = method.IsContractImplementation();

      // PT0001 – name must end with Async (for named methods),
      // but we DO NOT enforce it on contract implementations (MediatR Handle, overrides, etc.).
      if (!displayName.Equals("anonymous function", StringComparison.Ordinal) &&
          !displayName.EndsWith("Async", StringComparison.Ordinal) &&
          !isContract)
      {
         report(Diagnostic.Create(AsyncSuffixRule, location, displayName));
      }

      var ctInfo = GetCancellationTokenInfo(method);

      // PT0002 – missing CancellationToken
      // Only enforced on non-contract methods (e.g. interface itself, normal class methods).
      if (!ctInfo.HasCt)
      {
         if (!isContract)
         {
            report(Diagnostic.Create(CancellationTokenMissingRule, location, displayName));
         }

         return;
      }

      // PT0003 – name must be ct
// Enforced only on non-contract methods (interfaces / non-contract class methods).
// Implementations of contracts (overrides / interface impls) are skipped so we don't
// fight the “implementation params must match interface” rule or external contracts.
      if (!ctInfo.IsNamedCt && !isContract)
      {
         report(Diagnostic.Create(CancellationTokenNameRule, location, displayName, ctInfo.Name));
      }

      // PT0004 – CT must be last
      // Only enforced on non-contract methods (interface / own class methods).
      if (!ctInfo.IsLast && !isContract)
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

   private static CtInfo GetCancellationTokenInfo(IMethodSymbol method)
   {
      IParameterSymbol? ctParam = null;
      var parameters = method.Parameters;

      foreach (var p in parameters)
      {
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

      // "Last" means: last non-params parameter (params must stay physically last)
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

   private static void AnalyzeMinimalApiInvocation(OperationAnalysisContext context)
   {
      var invocation = (IInvocationOperation)context.Operation;
      var target = invocation.TargetMethod;

      // 1. Is this one of the Minimal API Map* extension methods?
      if (!IsMinimalApiMapMethod(target))
      {
         return;
      }

      // 2. Find the handler lambda inside the arguments.
      //    We do NOT rely on arg.Parameter.Type (which is often System.Delegate).
      IAnonymousFunctionOperation? handlerAnon = null;

      foreach (var arg in invocation.Arguments)
      {
         handlerAnon = ExtractAnonymousFunction(arg.Value);
         if (handlerAnon is not null)
         {
            break;
         }
      }

      if (handlerAnon is null)
      {
         // Handler is a method group or something non-lambda; skip.
         return;
      }

      var handlerSymbol = handlerAnon.Symbol;

      // Must be Task/ValueTask-based handler.
      if (handlerSymbol.ReturnType is not INamedTypeSymbol returnType ||
          !IsTaskLike(returnType))
      {
         return;
      }

      // 3. Enforce: minimal API handler must have a CancellationToken parameter.
      var ctInfo = GetCancellationTokenInfo(handlerSymbol);
      if (ctInfo.HasCt)
      {
         // If CT already exists, PT0003/PT0004 are handled by AnalyzeAnonymousFunction.
         return;
      }

      const string displayName = "anonymous function";

      context.ReportDiagnostic(
         Diagnostic.Create(
            CancellationTokenMissingRule,
            handlerAnon.Syntax.GetLocation(),
            displayName));
   }


   private static bool IsMinimalApiMapMethod(IMethodSymbol method)
   {
      if (!method.IsExtensionMethod)
      {
         return false;
      }

      // EndpointRouteBuilderExtensions.MapGet / MapPost / MapPut / MapDelete / MapMethods / ...
      var containingType = method.ContainingType;
      if (containingType is null)
      {
         return false;
      }

      if (!string.Equals(containingType.Name, "EndpointRouteBuilderExtensions", StringComparison.Ordinal))
      {
         return false;
      }

      var ns = containingType.ContainingNamespace.ToDisplayString();
      if (!ns.StartsWith("Microsoft.AspNetCore.Builder", StringComparison.Ordinal))
      {
         return false;
      }

      // Limit to the typical Map* names
      return method.Name is
         "MapGet" or
         "MapPost" or
         "MapPut" or
         "MapDelete" or
         "MapPatch" or
         "MapHead" or
         "MapOptions" or
         "MapTrace" or
         "MapMethods";
   }


   private static IAnonymousFunctionOperation? ExtractAnonymousFunction(IOperation value)
   {
      while (true)
      {
         switch (value)
         {
            case IAnonymousFunctionOperation anon:
               return anon;

            case IDelegateCreationOperation
            {
               Target: IAnonymousFunctionOperation anon
            }:
               return anon;

            case IConversionOperation
            {
               Operand: var operand
            }:
               value = operand;
               continue;

            default:
               return null;
         }
      }
   }

   private readonly struct CtInfo(bool hasCt, bool isNamedCt, bool isLast, string name)
   {
      public bool HasCt { get; } = hasCt;
      public bool IsNamedCt { get; } = isNamedCt;
      public bool IsLast { get; } = isLast;
      public string Name { get; } = name;
   }
}