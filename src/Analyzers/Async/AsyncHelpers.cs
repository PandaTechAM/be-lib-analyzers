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

      /// <summary>
      /// Checks if the type inherits from SignalR Hub or Hub{T}.
      /// </summary>
      private bool IsSignalRHub()
      {
         if (type is not INamedTypeSymbol namedType)
         {
            return false;
         }

         var current = namedType.BaseType;
         while (current is not null)
         {
            if (current.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.SignalR" &&
                current.Name == "Hub")
            {
               return true;
            }

            current = current.BaseType;
         }

         return false;
      }

      /// <summary>
      /// Checks if this interface is used as a client interface in SignalR Hub{TClient}.
      /// This detects interfaces like IChatClient in Hub{IChatClient}.
      /// </summary>
      private bool IsSignalRClientInterface(Compilation compilation)
      {
         if (type is not INamedTypeSymbol interfaceType || interfaceType.TypeKind != TypeKind.Interface)
         {
            return false;
         }

         // Search all types in the compilation for Hub<T> where T is this interface
         return ITypeSymbol.IsUsedAsSignalRClientInterface(interfaceType, compilation.GlobalNamespace);
      }

      /// <summary>
      /// Checks if this interface is implemented by a SignalR Hub class.
      /// This detects interfaces like IChatHub that are implemented by Hub-derived classes.
      /// </summary>
      private bool IsSignalRHubInterface(Compilation compilation)
      {
         if (type is not INamedTypeSymbol interfaceType || interfaceType.TypeKind != TypeKind.Interface)
         {
            return false;
         }

         // Search all types in the compilation for Hub classes that implement this interface
         return ITypeSymbol.IsImplementedBySignalRHub(interfaceType, compilation.GlobalNamespace);
      }

      private static bool IsImplementedBySignalRHub(INamedTypeSymbol interfaceType, INamespaceSymbol ns)
      {
         foreach (var member in ns.GetMembers())
         {
            switch (member)
            {
               case INamespaceSymbol childNs:
                  if (ITypeSymbol.IsImplementedBySignalRHub(interfaceType, childNs))
                  {
                     return true;
                  }

                  break;

               case INamedTypeSymbol namedType:
                  if (namedType.IsSignalRHub() &&
                      namedType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType)))
                  {
                     return true;
                  }

                  // Check nested types
                  foreach (var nested in namedType.GetTypeMembers())
                  {
                     if (nested.IsSignalRHub() &&
                         nested.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType)))
                     {
                        return true;
                     }
                  }

                  break;
            }
         }

         return false;
      }

      private static bool IsUsedAsSignalRClientInterface(INamedTypeSymbol interfaceType, INamespaceSymbol ns)
      {
         foreach (var member in ns.GetMembers())
         {
            switch (member)
            {
               case INamespaceSymbol childNs:
                  if (ITypeSymbol.IsUsedAsSignalRClientInterface(interfaceType, childNs))
                  {
                     return true;
                  }

                  break;

               case INamedTypeSymbol namedType:
                  if (ITypeSymbol.IsHubWithClientInterface(namedType, interfaceType))
                  {
                     return true;
                  }

                  // Check nested types
                  foreach (var nested in namedType.GetTypeMembers())
                  {
                     if (ITypeSymbol.IsHubWithClientInterface(nested, interfaceType))
                     {
                        return true;
                     }
                  }

                  break;
            }
         }

         return false;
      }

      private static bool IsHubWithClientInterface(INamedTypeSymbol typeToCheck, INamedTypeSymbol interfaceType)
      {
         var current = typeToCheck.BaseType;
         while (current is not null)
         {
            if (current.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.SignalR" &&
                current is
                {
                   Name: "Hub",
                   IsGenericType: true,
                   TypeArguments.Length: 1
                })
            {
               var clientType = current.TypeArguments[0];
               if (SymbolEqualityComparer.Default.Equals(clientType, interfaceType))
               {
                  return true;
               }
            }

            current = current.BaseType;
         }

         return false;
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

      /// <summary>
      /// Checks if the method is a SignalR hub method (method in a class that inherits from Hub).
      /// These methods are callable from clients and changing their signature breaks the contract.
      /// </summary>
      internal bool IsSignalRHubMethod()
      {
         var containingType = method.ContainingType;
         
         if (containingType is null)
         {
            return false;
         }

         // Must be public and in a Hub-derived class
         return method.DeclaredAccessibility == Accessibility.Public && containingType.IsSignalRHub();
      }

      /// <summary>
      /// Checks if this method is part of a SignalR client interface.
      /// These methods are invoked by the server to clients and cannot be renamed.
      /// </summary>
      internal bool IsSignalRClientMethod(Compilation compilation)
      {
         var containingType = method.ContainingType;
         if (containingType is null || containingType.TypeKind != TypeKind.Interface)
         {
            return false;
         }

         return containingType.IsSignalRClientInterface(compilation);
      }

      /// <summary>
      /// Checks if this method is part of an interface implemented by a SignalR Hub.
      /// These methods are callable from clients and cannot be renamed.
      /// </summary>
      internal bool IsSignalRHubInterfaceMethod(Compilation compilation)
      {
         var containingType = method.ContainingType;
         if (containingType is null || containingType.TypeKind != TypeKind.Interface)
         {
            return false;
         }

         return containingType.IsSignalRHubInterface(compilation);
      }

      /// <summary>
      /// Checks if the method is virtual or abstract (can be overridden).
      /// </summary>
      internal bool IsVirtualOrAbstract()
      {
         return method.IsVirtual || method.IsAbstract;
      }
   }
}