using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Rename;

namespace Pandatech.Analyzers.Async;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AsyncMethodConventionsCodeFixProvider))]
[Shared]
public sealed class AsyncMethodConventionsCodeFixProvider : CodeFixProvider
{
   public override ImmutableArray<string> FixableDiagnosticIds { get; } =
      ImmutableArray.Create(
         AsyncMethodConventionsAnalyzer.AsyncSuffixId,
         AsyncMethodConventionsAnalyzer.CancellationTokenMissingId,
         AsyncMethodConventionsAnalyzer.CancellationTokenNameId,
         AsyncMethodConventionsAnalyzer.CancellationTokenPositionId
      );

   public override FixAllProvider GetFixAllProvider()
   {
      return WellKnownFixAllProviders.BatchFixer;
   }

   public override async Task RegisterCodeFixesAsync(CodeFixContext context)
   {
      var diagnostic = context.Diagnostics[0];
      var diagnosticId = diagnostic.Id;
      var cancellationToken = context.CancellationToken;

      var document = context.Document;

      var root = await document.GetSyntaxRootAsync(cancellationToken)
                               .ConfigureAwait(false);
      if (root is null)
      {
         return;
      }

      var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                                        .ConfigureAwait(false);
      if (semanticModel is null)
      {
         return;
      }

      var span = diagnostic.Location.SourceSpan;
      var node = root.FindNode(span, getInnermostNodeForTie: true);

      var lambdaNode = node.FirstAncestorOrSelf<ParenthesizedLambdaExpressionSyntax>() ??
                       (CSharpSyntaxNode?)node.FirstAncestorOrSelf<SimpleLambdaExpressionSyntax>() ??
                       node.FirstAncestorOrSelf<AnonymousMethodExpressionSyntax>();

      if (lambdaNode is not null &&
          diagnosticId is AsyncMethodConventionsAnalyzer.CancellationTokenMissingId
             or AsyncMethodConventionsAnalyzer.CancellationTokenNameId
             or AsyncMethodConventionsAnalyzer.CancellationTokenPositionId)
      {
         RegisterLambdaCancellationTokenFixes(
            context,
            diagnostic,
            lambdaNode,
            semanticModel,
            cancellationToken);

         return;
      }

      var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
      if (methodDecl is null || !methodDecl.Identifier.Span.IntersectsWith(span))
      {
         return;
      }

      var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
      if (methodSymbol is null)
      {
         return;
      }

      switch (diagnosticId)
      {
         case AsyncMethodConventionsAnalyzer.AsyncSuffixId:
         {
            RegisterAsyncSuffixFix(context, diagnostic, methodSymbol);
            break;
         }

         case AsyncMethodConventionsAnalyzer.CancellationTokenMissingId:
         case AsyncMethodConventionsAnalyzer.CancellationTokenNameId:
         case AsyncMethodConventionsAnalyzer.CancellationTokenPositionId:
         {
            RegisterCancellationTokenFixesForMethod(
               context,
               diagnostic,
               methodDecl,
               methodSymbol,
               diagnosticId);
            break;
         }
      }
   }

   private static void RegisterAsyncSuffixFix(CodeFixContext context,
      Diagnostic diagnostic,
      IMethodSymbol methodSymbol)
   {
      if (methodSymbol.IsContractImplementation())
      {
         return;
      }

      var currentName = methodSymbol.Name;
      if (currentName.EndsWith("Async", StringComparison.Ordinal))
      {
         return;
      }

      var newName = currentName + "Async";
      var title = $"Rename '{currentName}' to '{newName}'";

      context.RegisterCodeFix(
         CodeAction.Create(
            title,
            c => RenameMethodAsync(context.Document.Project.Solution, methodSymbol, newName, c),
            "RenameToAsync"),
         diagnostic);
   }

   private static void RegisterCancellationTokenFixesForMethod(CodeFixContext context,
      Diagnostic diagnostic,
      MethodDeclarationSyntax methodDecl,
      IMethodSymbol methodSymbol,
      string diagnosticId)
   {
      if (methodSymbol.IsMiddleware())
      {
         return;
      }

      if (diagnosticId == AsyncMethodConventionsAnalyzer.CancellationTokenMissingId)
      {
         const string title = "Add CancellationToken ct parameter";

         var isInterfaceMethod = methodSymbol.ContainingType?.TypeKind == TypeKind.Interface;

         if (isInterfaceMethod)
         {
            context.RegisterCodeFix(
               CodeAction.Create(
                  title,
                  c => AddCancellationTokenForInterfaceAndImplementationsAsync(
                     context.Document.Project.Solution,
                     methodSymbol,
                     c),
                  "AddCtParameter_InterfaceAndImpls"),
               diagnostic);
         }
         else
         {
            context.RegisterCodeFix(
               CodeAction.Create(
                  title,
                  c => AddCancellationTokenAsync(context.Document, methodDecl, c),
                  "AddCtParameter"),
               diagnostic);
         }

         return;
      }

      var ctParam = methodSymbol.Parameters.FirstOrDefault(p => p.Type.IsCancellationToken());
      if (ctParam is null)
      {
         return;
      }

      switch (diagnosticId)
      {
         case AsyncMethodConventionsAnalyzer.CancellationTokenNameId
            when string.Equals(ctParam.Name, "ct", StringComparison.Ordinal):
            return;

         case AsyncMethodConventionsAnalyzer.CancellationTokenNameId:
         {
            var isInterfaceMethod = methodSymbol.ContainingType?.TypeKind == TypeKind.Interface;

            if (isInterfaceMethod)
            {
               const string title = "Rename CancellationToken parameter to 'ct' (interface and implementations)";

               context.RegisterCodeFix(
                  CodeAction.Create(
                     title,
                     c => RenameCancellationTokenForInterfaceAndImplementationsAsync(
                        context.Document.Project.Solution,
                        methodSymbol,
                        c),
                     "RenameCt_InterfaceAndImpls"),
                  diagnostic);

               return;
            }

            if (string.Equals(ctParam.Name, "ct", StringComparison.Ordinal))
            {
               return;
            }

            var renameTitle = $"Rename '{ctParam.Name}' to 'ct'";

            context.RegisterCodeFix(
               CodeAction.Create(
                  renameTitle,
                  c => RenameParameterAsync(context.Document.Project.Solution, ctParam, "ct", c),
                  "RenameCtParameter"),
               diagnostic);

            return;
         }

         case AsyncMethodConventionsAnalyzer.CancellationTokenPositionId:
         {
            if (ctParam.Ordinal == methodSymbol.Parameters.Length - 1)
            {
               return;
            }

            const string moveTitle = "Move CancellationToken parameter to last position";
            var isInterfaceMethod = methodSymbol.ContainingType?.TypeKind == TypeKind.Interface;

            if (isInterfaceMethod)
            {
               context.RegisterCodeFix(
                  CodeAction.Create(
                     moveTitle,
                     c => MoveCancellationTokenToLastForInterfaceAndImplementationsAsync(
                        context.Document.Project.Solution,
                        methodSymbol,
                        c),
                     "MoveCtParameterLast_InterfaceAndImpls"),
                  diagnostic);
            }
            else
            {
               context.RegisterCodeFix(
                  CodeAction.Create(
                     moveTitle,
                     c => MoveCancellationTokenToLastAsync(context.Document, methodDecl, ctParam, c),
                     "MoveCtParameterLast"),
                  diagnostic);
            }

            break;
         }
      }
   }

   private static void RegisterLambdaCancellationTokenFixes(CodeFixContext context,
      Diagnostic diagnostic,
      CSharpSyntaxNode lambdaNode,
      SemanticModel semanticModel,
      CancellationToken cancellationToken)
   {
      if (lambdaNode is not ExpressionSyntax lambdaExpr)
      {
         return;
      }

      var operation = semanticModel.GetOperation(lambdaExpr, cancellationToken) as IAnonymousFunctionOperation;
      if (operation is null)
      {
         return;
      }

      var symbol = operation.Symbol;
      var ctParam = symbol.Parameters.FirstOrDefault(p => p.Type.IsCancellationToken());

      switch (diagnostic.Id)
      {
         case AsyncMethodConventionsAnalyzer.CancellationTokenMissingId:
         {
            const string title = "Add CancellationToken ct parameter";

            context.RegisterCodeFix(
               CodeAction.Create(
                  title,
                  c => AddCancellationTokenToLambdaAsync(context.Document, lambdaExpr, c),
                  "AddCtParameterToLambda"),
               diagnostic);

            break;
         }

         case AsyncMethodConventionsAnalyzer.CancellationTokenNameId:
         {
            if (ctParam is null || string.Equals(ctParam.Name, "ct", StringComparison.Ordinal))
            {
               return;
            }

            var renameTitle = $"Rename '{ctParam.Name}' to 'ct'";

            context.RegisterCodeFix(
               CodeAction.Create(
                  renameTitle,
                  c => RenameParameterAsync(context.Document.Project.Solution, ctParam, "ct", c),
                  "RenameLambdaCtParameter"),
               diagnostic);

            break;
         }

         case AsyncMethodConventionsAnalyzer.CancellationTokenPositionId:
         {
            if (ctParam is null)
            {
               return;
            }

            var moveTitle = "Move CancellationToken parameter to last position";

            context.RegisterCodeFix(
               CodeAction.Create(
                  moveTitle,
                  c => MoveCancellationTokenToLastInLambdaAsync(context.Document, lambdaExpr, ctParam, c),
                  "MoveLambdaCtParameterLast"),
               diagnostic);

            break;
         }
      }
   }

   private static Task<Solution> RenameMethodAsync(Solution solution,
      IMethodSymbol methodSymbol,
      string newName,
      CancellationToken cancellationToken)
   {
      return Renamer.RenameSymbolAsync(
         solution,
         methodSymbol,
         new SymbolRenameOptions(),
         newName,
         cancellationToken);
   }

   private static Task<Solution> RenameParameterAsync(Solution solution,
      IParameterSymbol parameter,
      string newName,
      CancellationToken cancellationToken)
   {
      return Renamer.RenameSymbolAsync(
         solution,
         parameter,
         new SymbolRenameOptions(),
         newName,
         cancellationToken);
   }

   private static async Task<Document> AddCancellationTokenAsync(Document document,
      MethodDeclarationSyntax methodDecl,
      CancellationToken cancellationToken)
   {
      var root = await document.GetSyntaxRootAsync(cancellationToken)
                               .ConfigureAwait(false);
      if (root is not CompilationUnitSyntax compilationUnit)
      {
         return document;
      }

      var newRoot = compilationUnit;

      if (!compilationUnit.HasUsing("System.Threading"))
      {
         var usingDirective = SyntaxFactory.UsingDirective(
                                              SyntaxFactory.ParseName("System.Threading"))
                                           .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

         newRoot = newRoot.AddUsings(usingDirective);
      }

      var parameters = methodDecl.ParameterList.Parameters;

      var ctType = SyntaxFactory.IdentifierName("CancellationToken");
      var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"))
                                 .WithType(ctType)
                                 .WithDefault(
                                    SyntaxFactory.EqualsValueClause(
                                       SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

      var insertIndex = parameters.Count;
      for (var i = 0; i < parameters.Count; i++)
      {
         if (!parameters[i]
              .Modifiers
              .Any(SyntaxKind.ParamsKeyword))
         {
            continue;
         }

         insertIndex = i;
         break;
      }

      var newParameters = parameters.Insert(insertIndex, ctParam);
      var newMethod = methodDecl.WithParameterList(
         methodDecl.ParameterList.WithParameters(newParameters));

      newRoot = newRoot.ReplaceNode(methodDecl, newMethod);
      return document.WithSyntaxRoot(newRoot);
   }

   private static async Task<Document> MoveCancellationTokenToLastAsync(Document document,
      MethodDeclarationSyntax methodDecl,
      IParameterSymbol ctSymbol,
      CancellationToken cancellationToken)
   {
      var parameters = methodDecl.ParameterList.Parameters;

      var ctIndex = -1;
      for (var i = 0; i < parameters.Count; i++)
      {
         if (!string.Equals(parameters[i].Identifier.Text, ctSymbol.Name, StringComparison.Ordinal))
         {
            continue;
         }

         ctIndex = i;
         break;
      }

      if (ctIndex < 0)
      {
         return document;
      }

      var paramsIndex = -1;
      for (var i = 0; i < parameters.Count; i++)
      {
         if (!parameters[i]
              .Modifiers
              .Any(SyntaxKind.ParamsKeyword))
         {
            continue;
         }

         paramsIndex = i;
         break;
      }

      var alreadyCorrect =
         (paramsIndex < 0 && ctIndex == parameters.Count - 1) ||
         (paramsIndex >= 0 && ctIndex == paramsIndex - 1);

      if (alreadyCorrect)
      {
         return document;
      }

      var ctSyntax = parameters[ctIndex];
      var withoutCt = parameters.RemoveAt(ctIndex);

      var newParamsIndex = -1;
      for (var i = 0; i < withoutCt.Count; i++)
      {
         if (!withoutCt[i]
              .Modifiers
              .Any(SyntaxKind.ParamsKeyword))
         {
            continue;
         }

         newParamsIndex = i;
         break;
      }

      var newParameters =
         newParamsIndex >= 0
            ? withoutCt.Insert(newParamsIndex, ctSyntax)
            : withoutCt.Add(ctSyntax);

      var newMethod = methodDecl.WithParameterList(
         methodDecl.ParameterList.WithParameters(newParameters));

      var root = await document.GetSyntaxRootAsync(cancellationToken)
                               .ConfigureAwait(false);
      if (root is null)
      {
         return document;
      }

      var newRoot = root.ReplaceNode(methodDecl, newMethod);
      return document.WithSyntaxRoot(newRoot);
   }

   private static async Task<Document> AddCancellationTokenToLambdaAsync(Document document,
      ExpressionSyntax lambdaExpr,
      CancellationToken cancellationToken)
   {
      var root = await document.GetSyntaxRootAsync(cancellationToken)
                               .ConfigureAwait(false);
      if (root is null)
      {
         return document;
      }

      switch (lambdaExpr)
      {
         case ParenthesizedLambdaExpressionSyntax parenthesized:
         {
            var parameters = parenthesized.ParameterList.Parameters;

            var first = parameters.FirstOrDefault();
            var explicitParams = first is { Type: not null };

            var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"));
            if (explicitParams)
            {
               ctParam = ctParam.WithType(SyntaxFactory.IdentifierName("CancellationToken"));
            }

            var newParameters = parameters.Add(ctParam);

            var newLambda = parenthesized.WithParameterList(
               parenthesized.ParameterList.WithParameters(newParameters));

            var newRoot = root.ReplaceNode(parenthesized, newLambda);
            return document.WithSyntaxRoot(newRoot);
         }

         case SimpleLambdaExpressionSyntax simple:
         {
            var explicitParam = simple.Parameter.Type is not null;

            var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"));
            if (explicitParam)
            {
               ctParam = ctParam.WithType(SyntaxFactory.IdentifierName("CancellationToken"));
            }

            var paramList = SyntaxFactory.ParameterList(
               SyntaxFactory.SeparatedList([
                  simple.Parameter,
                  ctParam
               ]));

            var newLambda = SyntaxFactory.ParenthesizedLambdaExpression(paramList, simple.Body)
                                         .WithAttributeLists(simple.AttributeLists)
                                         .WithModifiers(simple.Modifiers)
                                         .WithAsyncKeyword(simple.AsyncKeyword)
                                         .WithTriviaFrom(simple);

            var newRoot = root.ReplaceNode(simple, newLambda);
            return document.WithSyntaxRoot(newRoot);
         }

         default:
            return document;
      }
   }

   private static async Task<Solution> AddCancellationTokenForInterfaceAndImplementationsAsync(Solution solution,
      IMethodSymbol interfaceMethod,
      CancellationToken cancellationToken)
   {
      var methodsByDocument = await GetInterfaceAndImplementationDeclarationsAsync(
            solution,
            interfaceMethod,
            cancellationToken)
         .ConfigureAwait(false);

      foreach (var kvp in methodsByDocument)
      {
         var docId = kvp.Key;
         var methodDecls = kvp.Value;

         var document = solution.GetDocument(docId);
         if (document is null)
         {
            continue;
         }

         var root = await document.GetSyntaxRootAsync(cancellationToken)
                                  .ConfigureAwait(false);
         if (root is not CompilationUnitSyntax compilationUnit)
         {
            continue;
         }

         var updatedRoot = compilationUnit;

         if (!compilationUnit.HasUsing("System.Threading"))
         {
            var usingDirective = SyntaxFactory.UsingDirective(
                                                 SyntaxFactory.ParseName("System.Threading"))
                                              .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

            updatedRoot = updatedRoot.AddUsings(usingDirective);
         }

         var ctType = SyntaxFactory.IdentifierName("CancellationToken");

         updatedRoot = updatedRoot.ReplaceNodes(
            methodDecls,
            (original, _) =>
            {
               foreach (var p in original.ParameterList.Parameters)
               {
                  if (p.Type is IdentifierNameSyntax id &&
                      string.Equals(id.Identifier.Text, "CancellationToken", StringComparison.Ordinal))
                  {
                     return original;
                  }
               }

               var parameters = original.ParameterList.Parameters;

               var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"))
                                          .WithType(ctType)
                                          .WithDefault(
                                             SyntaxFactory.EqualsValueClause(
                                                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

               var insertIndex = parameters.Count;
               for (var i = 0; i < parameters.Count; i++)
               {
                  if (!parameters[i]
                       .Modifiers
                       .Any(SyntaxKind.ParamsKeyword))
                  {
                     continue;
                  }

                  insertIndex = i;
                  break;
               }

               var newParameters = parameters.Insert(insertIndex, ctParam);

               return original.WithParameterList(
                  original.ParameterList.WithParameters(newParameters));
            });

         solution = solution.WithDocumentSyntaxRoot(docId, updatedRoot);
      }

      return solution;
   }

   private static async Task<Solution> MoveCancellationTokenToLastForInterfaceAndImplementationsAsync(Solution solution,
      IMethodSymbol interfaceMethod,
      CancellationToken cancellationToken)
   {
      var methodsByDocument = await GetInterfaceAndImplementationDeclarationsAsync(
            solution,
            interfaceMethod,
            cancellationToken)
         .ConfigureAwait(false);

      foreach (var kvp in methodsByDocument)
      {
         var docId = kvp.Key;
         var methodDecls = kvp.Value;

         var document = solution.GetDocument(docId);
         if (document is null)
         {
            continue;
         }

         var root = await document.GetSyntaxRootAsync(cancellationToken)
                                  .ConfigureAwait(false);
         if (root is null)
         {
            continue;
         }

         var newRoot = root.ReplaceNodes(
            methodDecls,
            (original, _) =>
            {
               var parameters = original.ParameterList.Parameters;

               var ctIndex = -1;
               for (var i = 0; i < parameters.Count; i++)
               {
                  if (parameters[i].Type is not IdentifierNameSyntax id ||
                      !string.Equals(id.Identifier.Text, "CancellationToken", StringComparison.Ordinal))
                  {
                     continue;
                  }

                  ctIndex = i;
                  break;
               }

               if (ctIndex < 0)
               {
                  return original;
               }

               var paramsIndex = -1;
               for (var i = 0; i < parameters.Count; i++)
               {
                  if (parameters[i]
                      .Modifiers
                      .Any(SyntaxKind.ParamsKeyword))
                  {
                     paramsIndex = i;
                     break;
                  }
               }

               var alreadyCorrect =
                  (paramsIndex < 0 && ctIndex == parameters.Count - 1) ||
                  (paramsIndex >= 0 && ctIndex == paramsIndex - 1);

               if (alreadyCorrect)
               {
                  return original;
               }

               var ctSyntax = parameters[ctIndex];
               var withoutCt = parameters.RemoveAt(ctIndex);

               var newParamsIndex = -1;
               for (var i = 0; i < withoutCt.Count; i++)
               {
                  if (!withoutCt[i]
                       .Modifiers
                       .Any(SyntaxKind.ParamsKeyword))
                  {
                     continue;
                  }

                  newParamsIndex = i;
                  break;
               }

               var newParameters = newParamsIndex >= 0
                  ? withoutCt.Insert(newParamsIndex, ctSyntax)
                  : withoutCt.Add(ctSyntax);

               return original.WithParameterList(
                  original.ParameterList.WithParameters(newParameters));
            });

         solution = solution.WithDocumentSyntaxRoot(docId, newRoot);
      }

      return solution;
   }

   private static async Task<Document> MoveCancellationTokenToLastInLambdaAsync(Document document,
      ExpressionSyntax lambdaExpr,
      IParameterSymbol ctSymbol,
      CancellationToken cancellationToken)
   {
      var root = await document.GetSyntaxRootAsync(cancellationToken)
                               .ConfigureAwait(false);

      if (root is null || lambdaExpr is not ParenthesizedLambdaExpressionSyntax parenthesized)
      {
         return document;
      }

      var parameters = parenthesized.ParameterList.Parameters;

      var ctIndex = -1;
      for (var i = 0; i < parameters.Count; i++)
      {
         if (!string.Equals(parameters[i].Identifier.Text, ctSymbol.Name, StringComparison.Ordinal))
         {
            continue;
         }

         ctIndex = i;
         break;
      }

      if (ctIndex < 0 || ctIndex == parameters.Count - 1)
      {
         return document;
      }

      var ctSyntax = parameters[ctIndex];
      var newParameters = parameters.RemoveAt(ctIndex)
                                    .Add(ctSyntax);

      var newLambda = parenthesized.WithParameterList(
         parenthesized.ParameterList.WithParameters(newParameters));

      var newRoot = root.ReplaceNode(parenthesized, newLambda);
      return document.WithSyntaxRoot(newRoot);
   }

   private static async Task<Solution> RenameCancellationTokenForInterfaceAndImplementationsAsync(Solution solution,
      IMethodSymbol interfaceMethod,
      CancellationToken cancellationToken)
   {
      var ctIndex = -1;
      for (var i = 0; i < interfaceMethod.Parameters.Length; i++)
      {
         if (!interfaceMethod.Parameters[i]
                             .Type
                             .IsCancellationToken())
         {
            continue;
         }

         ctIndex = i;
         break;
      }

      if (ctIndex < 0)
      {
         return solution;
      }

      var methodIds = new List<string>();

      var interfaceId = interfaceMethod.GetDocumentationCommentId();
      if (!string.IsNullOrEmpty(interfaceId))
      {
         methodIds.Add(interfaceId!);
      }

      var impls = await SymbolFinder.FindImplementationsAsync(
                                       interfaceMethod,
                                       solution,
                                       null,
                                       cancellationToken)
                                    .ConfigureAwait(false);

      foreach (var impl in impls.OfType<IMethodSymbol>())
      {
         if (impl.DeclaringSyntaxReferences.Length == 0)
         {
            continue;
         }

         var id = impl.GetDocumentationCommentId();
         if (!string.IsNullOrEmpty(id))
         {
            methodIds.Add(id!);
         }
      }

      foreach (var methodId in methodIds)
      {
         IMethodSymbol? methodSymbol = null;

         foreach (var project in solution.Projects)
         {
            var compilation = await project.GetCompilationAsync(cancellationToken)
                                           .ConfigureAwait(false);
            if (compilation is null)
            {
               continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(methodId, compilation)
               as IMethodSymbol;

            if (symbol is null)
            {
               continue;
            }

            methodSymbol = symbol;
            break;
         }

         if (methodSymbol is null)
         {
            continue;
         }

         if (ctIndex >= methodSymbol.Parameters.Length)
         {
            continue;
         }

         var p = methodSymbol.Parameters[ctIndex];
         if (!p.Type.IsCancellationToken())
         {
            continue;
         }

         if (string.Equals(p.Name, "ct", StringComparison.Ordinal))
         {
            continue;
         }

         solution = await Renamer.RenameSymbolAsync(
                                    solution,
                                    p,
                                    new SymbolRenameOptions(),
                                    "ct",
                                    cancellationToken)
                                 .ConfigureAwait(false);
      }

      return solution;
   }

   private static async Task<Dictionary<DocumentId, ImmutableArray<MethodDeclarationSyntax>>>
      GetInterfaceAndImplementationDeclarationsAsync(Solution solution,
         IMethodSymbol interfaceMethod,
         CancellationToken cancellationToken)
   {
      var allMethods = ImmutableArray.CreateBuilder<IMethodSymbol>();
      allMethods.Add(interfaceMethod);

      var impls = await SymbolFinder.FindImplementationsAsync(
                                       interfaceMethod,
                                       solution,
                                       null,
                                       cancellationToken)
                                    .ConfigureAwait(false);

      foreach (var impl in impls.OfType<IMethodSymbol>())
      {
         if (impl.DeclaringSyntaxReferences.Length > 0)
         {
            allMethods.Add(impl);
         }
      }

      var methodsByDocument = new Dictionary<DocumentId, ImmutableArray<MethodDeclarationSyntax>.Builder>();

      foreach (var syntaxRef in allMethods.SelectMany(method => method.DeclaringSyntaxReferences))
      {
         var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken)
                                     .ConfigureAwait(false);

         if (syntax is not MethodDeclarationSyntax methodDecl)
         {
            continue;
         }

         var doc = solution.GetDocument(methodDecl.SyntaxTree);
         if (doc is null)
         {
            continue;
         }

         var docId = doc.Id;

         if (!methodsByDocument.TryGetValue(docId, out var list))
         {
            list = ImmutableArray.CreateBuilder<MethodDeclarationSyntax>();
            methodsByDocument[docId] = list;
         }

         list.Add(methodDecl);
      }

      var result = new Dictionary<DocumentId, ImmutableArray<MethodDeclarationSyntax>>();
      foreach (var kvp in methodsByDocument)
      {
         result[kvp.Key] = kvp.Value.ToImmutable();
      }

      return result;
   }
}