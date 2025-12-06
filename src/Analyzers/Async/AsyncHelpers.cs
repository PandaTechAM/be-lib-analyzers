using System.Linq;
using Microsoft.CodeAnalysis;

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
         if (member is not IMethodSymbol ifaceMethod)
         {
            continue;
         }

         var implementation = containingType.FindImplementationForInterfaceMember(ifaceMethod);
         if (SymbolEqualityComparer.Default.Equals(implementation, method))
         {
            return true;
         }
      }

      return false;
   }
}