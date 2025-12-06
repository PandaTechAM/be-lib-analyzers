using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Pandatech.Analyzers.Async;

internal static class AsyncHelpers
{
   internal static bool IsContractImplementation(this IMethodSymbol method)
   {
      if (method.IsOverride)
      {
         return true;
      }

      if (!method.ExplicitInterfaceImplementations.IsEmpty)
      {
         return true;
      }

      var containingType = method.ContainingType;
      if (containingType is null)
      {
         return false;
      }

      foreach (var member in containingType.AllInterfaces.SelectMany(iface => iface.GetMembers(method.Name)))
      {
         if (member is not IMethodSymbol interfaceMethod)
         {
            continue;
         }

         var impl = containingType.FindImplementationForInterfaceMember(interfaceMethod);
         if (SymbolEqualityComparer.Default.Equals(impl, method))
         {
            return true;
         }
      }

      return false;
   }

   internal static bool HasUsing(this CompilationUnitSyntax root, string namespaceName)
   {
      return root.Usings.Any(u => u.Name?.ToString() == namespaceName);
   }

   internal static bool IsMinimalApiMapMethod(this IMethodSymbol method)
   {
      if (!method.IsExtensionMethod)
      {
         return false;
      }

      var containingType = method.ContainingType;
      if (containingType is null || containingType.Name != "EndpointRouteBuilderExtensions")
      {
         return false;
      }

      var ns = containingType.ContainingNamespace.ToDisplayString();
      if (!ns.StartsWith("Microsoft.AspNetCore.Builder", StringComparison.Ordinal))
      {
         return false;
      }

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

   internal static IAnonymousFunctionOperation? ExtractAnonymousFunction(IOperation value)
   {
      while (true)
      {
         switch (value)
         {
            case IAnonymousFunctionOperation anon:
               return anon;

            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation anon }:
               return anon;

            case IConversionOperation { Operand: var operand }:
               value = operand;
               continue;

            default:
               return null;
         }
      }
   }

   extension(ITypeSymbol type)
   {
      internal bool IsTaskLike()
      {
         if (type is not INamedTypeSymbol named)
         {
            return false;
         }

         return named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
                named.Name is "Task" or "ValueTask";
      }

      internal bool IsCancellationToken()
      {
         if (type is not INamedTypeSymbol named)
         {
            return false;
         }

         return named.ContainingNamespace.ToDisplayString() == "System.Threading" &&
                named.Name == "CancellationToken";
      }
   }

   extension(IMethodSymbol method)
   {
      internal bool IsTestMethod()
      {
         foreach (var attr in method.GetAttributes())
         {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
               continue;
            }

            var name = attrClass.Name;
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
            {
               name = name.Substring(0, name.Length - "Attribute".Length);
            }

            if (name is "Fact"
                or "Theory"
                or "Test"
                or "TestCase"
                or "TestMethod"
                or "DataTestMethod")
            {
               return true;
            }
         }

         return false;
      }

      internal bool IsMiddleware()
      {
         if (method.Name is not ("Invoke" or "InvokeAsync"))
         {
            return false;
         }

         if (method.Parameters.Length == 0)
         {
            return false;
         }

         if (method.ReturnType is not INamedTypeSymbol returnType || !returnType.IsTaskLike())
         {
            return false;
         }

         if (method.Parameters[0].Type is not INamedTypeSymbol httpContextType)
         {
            return false;
         }

         return httpContextType.Name == "HttpContext" &&
                httpContextType.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.Http";
      }
   }
}