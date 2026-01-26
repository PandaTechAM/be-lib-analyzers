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

      var isInterfaceMethod = methodSymbol.ContainingType?.TypeKind == TypeKind.Interface;
      var isVirtualOrAbstract = methodSymbol.IsVirtualOrAbstract();
      var methodId = methodSymbol.GetDocumentationCommentId();

      if (string.IsNullOrEmpty(methodId))
      {
         return;
      }

      if (diagnosticId == AsyncMethodConventionsAnalyzer.CancellationTokenMissingId)
      {
         const string title = "Add CancellationToken ct parameter";

         if (isInterfaceMethod)
         {
            context.RegisterCodeFix(
               CodeAction.Create(
                  title,
                  c => AddCancellationTokenForHierarchyAsync(
                     context.Document.Project.Solution,
                     methodId!,
                     MethodHierarchyKind.Interface,
                     c),
                  "AddCtParameter_InterfaceAndImpls"),
               diagnostic);
         }
         else if (isVirtualOrAbstract)
         {
            context.RegisterCodeFix(
               CodeAction.Create(
                  title,
                  c => AddCancellationTokenForHierarchyAsync(
                     context.Document.Project.Solution,
                     methodId!,
                     MethodHierarchyKind.Virtual,
                     c),
                  "AddCtParameter_VirtualAndOverrides"),
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
            var ctIndex = ctParam.Ordinal;

            if (isInterfaceMethod)
            {
               const string title = "Rename CancellationToken parameter to 'ct' (interface and implementations)";

               context.RegisterCodeFix(
                  CodeAction.Create(
                     title,
                     c => RenameCancellationTokenForHierarchyAsync(
                        context.Document.Project.Solution,
                        methodId!,
                        ctIndex,
                        MethodHierarchyKind.Interface,
                        c),
                     "RenameCt_InterfaceAndImpls"),
                  diagnostic);

               return;
            }

            if (isVirtualOrAbstract)
            {
               const string title = "Rename CancellationToken parameter to 'ct' (base and overrides)";

               context.RegisterCodeFix(
                  CodeAction.Create(
                     title,
                     c => RenameCancellationTokenForHierarchyAsync(
                        context.Document.Project.Solution,
                        methodId!,
                        ctIndex,
                        MethodHierarchyKind.Virtual,
                        c),
                     "RenameCt_VirtualAndOverrides"),
                  diagnostic);

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

            if (isInterfaceMethod)
            {
               context.RegisterCodeFix(
                  CodeAction.Create(
                     moveTitle,
                     c => MoveCancellationTokenToLastForHierarchyAsync(
                        context.Document.Project.Solution,
                        methodId!,
                        MethodHierarchyKind.Interface,
                        c),
                     "MoveCtParameterLast_InterfaceAndImpls"),
                  diagnostic);
            }
            else if (isVirtualOrAbstract)
            {
               context.RegisterCodeFix(
                  CodeAction.Create(
                     moveTitle,
                     c => MoveCancellationTokenToLastForHierarchyAsync(
                        context.Document.Project.Solution,
                        methodId!,
                        MethodHierarchyKind.Virtual,
                        c),
                     "MoveCtParameterLast_VirtualAndOverrides"),
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

      var newRoot = EnsureUsingDirective(compilationUnit);
      var parameters = methodDecl.ParameterList.Parameters;

      var ctParam = CreateCancellationTokenParameter();
      var insertIndex = GetInsertIndexBeforeParams(parameters);

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

      var ctIndex = FindParameterIndex(parameters, ctSymbol.Name);
      if (ctIndex < 0)
      {
         return document;
      }

      var paramsIndex = FindParamsIndex(parameters);

      if (IsAlreadyInCorrectPosition(ctIndex, parameters.Count, paramsIndex))
      {
         return document;
      }

      var newParameters = MoveParameterToEnd(parameters, ctIndex, paramsIndex);
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
               SyntaxFactory.SeparatedList([simple.Parameter, ctParam]));

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

      var ctIndex = FindParameterIndex(parameters, ctSymbol.Name);
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

   private static async Task<Solution> AddCancellationTokenForHierarchyAsync(Solution solution,
      string rootMethodId,
      MethodHierarchyKind hierarchyKind,
      CancellationToken cancellationToken)
   {
      var methodIds = await CollectMethodIdsAsync(solution, rootMethodId, hierarchyKind, cancellationToken)
         .ConfigureAwait(false);

      var documentEdits = new Dictionary<DocumentId, List<string>>();

      // Group method IDs by document
      foreach (var methodId in methodIds)
      {
         var location = await FindMethodLocationAsync(solution, methodId, cancellationToken)
            .ConfigureAwait(false);

         if (location is null)
         {
            continue;
         }

         if (!documentEdits.TryGetValue(location.Value.DocumentId, out var list))
         {
            list = [];
            documentEdits[location.Value.DocumentId] = list;
         }

         list.Add(methodId);
      }

      // Process each document once with all its method edits
      foreach (var kvp in documentEdits)
      {
         var docId = kvp.Key;
         var methodIdsInDoc = kvp.Value;

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

         var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                                           .ConfigureAwait(false);
         if (semanticModel is null)
         {
            continue;
         }

         var updatedRoot = EnsureUsingDirective(compilationUnit);

         // Find all method declarations to update in this document
         var methodsToUpdate = new List<MethodDeclarationSyntax>();
         foreach (var methodId in methodIdsInDoc)
         {
            var methodDecl = FindMethodDeclarationById(updatedRoot, semanticModel, methodId, cancellationToken);
            if (methodDecl is not null && !HasCancellationTokenParameter(methodDecl))
            {
               methodsToUpdate.Add(methodDecl);
            }
         }

         if (methodsToUpdate.Count == 0)
         {
            continue;
         }

         updatedRoot = updatedRoot.ReplaceNodes(
            methodsToUpdate,
            (original, _) =>
            {
               var parameters = original.ParameterList.Parameters;
               var ctParam = CreateCancellationTokenParameter();
               var insertIndex = GetInsertIndexBeforeParams(parameters);
               var newParameters = parameters.Insert(insertIndex, ctParam);

               return original.WithParameterList(
                  original.ParameterList.WithParameters(newParameters));
            });

         solution = solution.WithDocumentSyntaxRoot(docId, updatedRoot);
      }

      return solution;
   }

   private static async Task<Solution> MoveCancellationTokenToLastForHierarchyAsync(Solution solution,
      string rootMethodId,
      MethodHierarchyKind hierarchyKind,
      CancellationToken cancellationToken)
   {
      var methodIds = await CollectMethodIdsAsync(solution, rootMethodId, hierarchyKind, cancellationToken)
         .ConfigureAwait(false);

      var documentEdits = new Dictionary<DocumentId, List<string>>();

      foreach (var methodId in methodIds)
      {
         var location = await FindMethodLocationAsync(solution, methodId, cancellationToken)
            .ConfigureAwait(false);

         if (location is null)
         {
            continue;
         }

         if (!documentEdits.TryGetValue(location.Value.DocumentId, out var list))
         {
            list = [];
            documentEdits[location.Value.DocumentId] = list;
         }

         list.Add(methodId);
      }

      foreach (var kvp in documentEdits)
      {
         var docId = kvp.Key;
         var methodIdsInDoc = kvp.Value;

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

         var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                                           .ConfigureAwait(false);
         if (semanticModel is null)
         {
            continue;
         }

         var methodsToUpdate = new List<MethodDeclarationSyntax>();
         foreach (var methodId in methodIdsInDoc)
         {
            var methodDecl = FindMethodDeclarationById(root, semanticModel, methodId, cancellationToken);
            if (methodDecl is null)
            {
               continue;
            }

            var ctIndex = FindCancellationTokenIndex(methodDecl.ParameterList.Parameters);
            if (ctIndex < 0)
            {
               continue;
            }

            var paramsIndex = FindParamsIndex(methodDecl.ParameterList.Parameters);
            if (!IsAlreadyInCorrectPosition(ctIndex, methodDecl.ParameterList.Parameters.Count, paramsIndex))
            {
               methodsToUpdate.Add(methodDecl);
            }
         }

         if (methodsToUpdate.Count == 0)
         {
            continue;
         }

         var newRoot = root.ReplaceNodes(
            methodsToUpdate,
            (original, _) =>
            {
               var parameters = original.ParameterList.Parameters;
               var ctIndex = FindCancellationTokenIndex(parameters);

               if (ctIndex < 0)
               {
                  return original;
               }

               var paramsIndex = FindParamsIndex(parameters);
               var newParameters = MoveParameterToEnd(parameters, ctIndex, paramsIndex);

               return original.WithParameterList(
                  original.ParameterList.WithParameters(newParameters));
            });

         solution = solution.WithDocumentSyntaxRoot(docId, newRoot);
      }

      return solution;
   }

   private static async Task<Solution> RenameCancellationTokenForHierarchyAsync(Solution solution,
      string rootMethodId,
      int ctParameterIndex,
      MethodHierarchyKind hierarchyKind,
      CancellationToken cancellationToken)
   {
      var methodIds = await CollectMethodIdsAsync(solution, rootMethodId, hierarchyKind, cancellationToken)
         .ConfigureAwait(false);

      // Process renames one at a time, re-resolving symbols after each rename
      foreach (var methodId in methodIds)
      {
         var methodSymbol = await ResolveMethodSymbolAsync(solution, methodId, cancellationToken)
            .ConfigureAwait(false);

         if (methodSymbol is null || ctParameterIndex >= methodSymbol.Parameters.Length)
         {
            continue;
         }

         var param = methodSymbol.Parameters[ctParameterIndex];
         if (!param.Type.IsCancellationToken())
         {
            continue;
         }

         if (string.Equals(param.Name, "ct", StringComparison.Ordinal))
         {
            continue;
         }

         solution = await Renamer.RenameSymbolAsync(
                                    solution,
                                    param,
                                    new SymbolRenameOptions(),
                                    "ct",
                                    cancellationToken)
                                 .ConfigureAwait(false);
      }

      return solution;
   }

   private static async Task<ImmutableArray<string>> CollectMethodIdsAsync(Solution solution,
      string rootMethodId,
      MethodHierarchyKind hierarchyKind,
      CancellationToken cancellationToken)
   {
      var methodIds = ImmutableArray.CreateBuilder<string>();
      methodIds.Add(rootMethodId);

      var rootMethod = await ResolveMethodSymbolAsync(solution, rootMethodId, cancellationToken)
         .ConfigureAwait(false);

      if (rootMethod is null)
      {
         return methodIds.ToImmutable();
      }

      var relatedMethods = hierarchyKind switch
      {
         MethodHierarchyKind.Interface => await SymbolFinder.FindImplementationsAsync(
                                                               rootMethod,
                                                               solution,
                                                               null,
                                                               cancellationToken)
                                                            .ConfigureAwait(false),

         MethodHierarchyKind.Virtual => await SymbolFinder.FindOverridesAsync(
                                                             rootMethod,
                                                             solution,
                                                             null,
                                                             cancellationToken)
                                                          .ConfigureAwait(false),

         _ => []
      };

      foreach (var related in relatedMethods.OfType<IMethodSymbol>())
      {
         if (related.DeclaringSyntaxReferences.Length == 0)
         {
            continue;
         }

         var id = related.GetDocumentationCommentId();
         if (!string.IsNullOrEmpty(id))
         {
            methodIds.Add(id!);
         }
      }

      return methodIds.ToImmutable();
   }

   private static async Task<IMethodSymbol?> ResolveMethodSymbolAsync(Solution solution,
      string methodId,
      CancellationToken cancellationToken)
   {
      foreach (var project in solution.Projects)
      {
         var compilation = await project.GetCompilationAsync(cancellationToken)
                                        .ConfigureAwait(false);
         if (compilation is null)
         {
            continue;
         }

         if (DocumentationCommentId.GetFirstSymbolForDeclarationId(methodId, compilation)
             is IMethodSymbol methodSymbol)
         {
            return methodSymbol;
         }
      }

      return null;
   }

   private static async Task<(DocumentId DocumentId, SyntaxReference Reference)?> FindMethodLocationAsync(
      Solution solution,
      string methodId,
      CancellationToken cancellationToken)
   {
      var methodSymbol = await ResolveMethodSymbolAsync(solution, methodId, cancellationToken)
         .ConfigureAwait(false);

      if (methodSymbol is null || methodSymbol.DeclaringSyntaxReferences.Length == 0)
      {
         return null;
      }

      var syntaxRef = methodSymbol.DeclaringSyntaxReferences[0];
      var document = solution.GetDocument(syntaxRef.SyntaxTree);

      if (document is null)
      {
         return null;
      }

      return (document.Id, syntaxRef);
   }

   private static MethodDeclarationSyntax? FindMethodDeclarationById(SyntaxNode root,
      SemanticModel semanticModel,
      string methodId,
      CancellationToken cancellationToken)
   {
      foreach (var methodDecl in root.DescendantNodes()
                                     .OfType<MethodDeclarationSyntax>())
      {
         var symbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
         if (symbol is null)
         {
            continue;
         }

         var id = symbol.GetDocumentationCommentId();
         if (string.Equals(id, methodId, StringComparison.Ordinal))
         {
            return methodDecl;
         }
      }

      return null;
   }

   private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax compilationUnit)
   {
      if (compilationUnit.HasUsing("System.Threading"))
      {
         return compilationUnit;
      }

      var usingDirective = SyntaxFactory.UsingDirective(
                                           SyntaxFactory.ParseName("System.Threading"))
                                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

      return compilationUnit.AddUsings(usingDirective);
   }

   private static ParameterSyntax CreateCancellationTokenParameter()
   {
      return SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"))
                          .WithType(SyntaxFactory.IdentifierName("CancellationToken"))
                          .WithDefault(
                             SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
   }

   private static int GetInsertIndexBeforeParams(SeparatedSyntaxList<ParameterSyntax> parameters)
   {
      for (var i = 0; i < parameters.Count; i++)
      {
         if (parameters[i]
             .Modifiers
             .Any(SyntaxKind.ParamsKeyword))
         {
            return i;
         }
      }

      return parameters.Count;
   }

   private static int FindParameterIndex(SeparatedSyntaxList<ParameterSyntax> parameters, string name)
   {
      for (var i = 0; i < parameters.Count; i++)
      {
         if (string.Equals(parameters[i].Identifier.Text, name, StringComparison.Ordinal))
         {
            return i;
         }
      }

      return -1;
   }

   private static int FindParamsIndex(SeparatedSyntaxList<ParameterSyntax> parameters)
   {
      for (var i = 0; i < parameters.Count; i++)
      {
         if (parameters[i]
             .Modifiers
             .Any(SyntaxKind.ParamsKeyword))
         {
            return i;
         }
      }

      return -1;
   }

   private static int FindCancellationTokenIndex(SeparatedSyntaxList<ParameterSyntax> parameters)
   {
      for (var i = 0; i < parameters.Count; i++)
      {
         if (parameters[i].Type is IdentifierNameSyntax id &&
             string.Equals(id.Identifier.Text, "CancellationToken", StringComparison.Ordinal))
         {
            return i;
         }
      }

      return -1;
   }

   private static bool HasCancellationTokenParameter(MethodDeclarationSyntax methodDecl)
   {
      return FindCancellationTokenIndex(methodDecl.ParameterList.Parameters) >= 0;
   }

   private static bool IsAlreadyInCorrectPosition(int ctIndex, int totalCount, int paramsIndex)
   {
      return (paramsIndex < 0 && ctIndex == totalCount - 1) ||
             (paramsIndex >= 0 && ctIndex == paramsIndex - 1);
   }

   private static SeparatedSyntaxList<ParameterSyntax> MoveParameterToEnd(
      SeparatedSyntaxList<ParameterSyntax> parameters,
      int indexToMove,
      int paramsIndex)
   {
      var paramToMove = parameters[indexToMove];
      var withoutParam = parameters.RemoveAt(indexToMove);

      // Recalculate params index after removal
      var newParamsIndex = -1;
      for (var i = 0; i < withoutParam.Count; i++)
      {
         if (!withoutParam[i]
              .Modifiers
              .Any(SyntaxKind.ParamsKeyword))
         {
            continue;
         }

         newParamsIndex = i;
         break;
      }

      return newParamsIndex >= 0
         ? withoutParam.Insert(newParamsIndex, paramToMove)
         : withoutParam.Add(paramToMove);
   }

   private enum MethodHierarchyKind
   {
      Interface,
      Virtual
   }
}