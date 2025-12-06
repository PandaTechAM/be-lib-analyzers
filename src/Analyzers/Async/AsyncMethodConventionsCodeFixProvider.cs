using System;
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

   public override FixAllProvider? GetFixAllProvider()
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

      // ------------------------------------------------------------------
      // 1. Lambda / anonymous function case (PT0002 / PT0003 / PT0004)
      // ------------------------------------------------------------------

      var lambdaNode = node.FirstAncestorOrSelf<ParenthesizedLambdaExpressionSyntax>() ??
                       (CSharpSyntaxNode?)node.FirstAncestorOrSelf<SimpleLambdaExpressionSyntax>();

      lambdaNode ??= node.FirstAncestorOrSelf<AnonymousMethodExpressionSyntax>();

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

      // ------------------------------------------------------------------
      // 2. Method / interface declaration case (your original logic)
      // ------------------------------------------------------------------

      var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
      if (methodDecl is null)
      {
         return;
      }

      if (!methodDecl.Identifier.Span.IntersectsWith(span))
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

   // ----------------------------------------------------------------------
   // PT0001: rename method to *Async
   // ----------------------------------------------------------------------

   private static void RegisterAsyncSuffixFix(CodeFixContext context,
      Diagnostic diagnostic,
      IMethodSymbol methodSymbol)
   {
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

   // ----------------------------------------------------------------------
   // PT0002 / PT0003 / PT0004 – methods & interfaces
   // ----------------------------------------------------------------------

   private static void RegisterCancellationTokenFixesForMethod(CodeFixContext context,
      Diagnostic diagnostic,
      MethodDeclarationSyntax methodDecl,
      IMethodSymbol methodSymbol,
      string diagnosticId)
   {
      if (diagnosticId == AsyncMethodConventionsAnalyzer.CancellationTokenMissingId)
      {
         var title = "Add CancellationToken ct parameter";

         context.RegisterCodeFix(
            CodeAction.Create(
               title,
               c => AddCancellationTokenAsync(context.Document, methodDecl, c),
               "AddCtParameter"),
            diagnostic);

         return;
      }

      var ctParam = methodSymbol.Parameters
                                .FirstOrDefault(p => IsCancellationToken(p.Type));

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
            if (ctParam.Ordinal != methodSymbol.Parameters.Length - 1)
            {
               const string moveTitle = "Move CancellationToken parameter to last position";

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

   // ----------------------------------------------------------------------
   // PT0002 / PT0003 / PT0004 – lambdas / anonymous functions
   // ----------------------------------------------------------------------

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
      var ctParam = symbol.Parameters.FirstOrDefault(p => IsCancellationToken(p.Type));

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

   // ----------------------------------------------------------------------
   // Symbol-level helpers
   // ----------------------------------------------------------------------

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

   private static bool IsCancellationToken(ITypeSymbol type)
   {
      if (type is not INamedTypeSymbol named)
      {
         return false;
      }

      if (!string.Equals(named.Name, "CancellationToken", StringComparison.Ordinal))
      {
         return false;
      }

      return string.Equals(
         named.ContainingNamespace.ToDisplayString(),
         "System.Threading",
         StringComparison.Ordinal);
   }

   // ----------------------------------------------------------------------
   // Syntax-level helpers – methods
   // ----------------------------------------------------------------------

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

      if (!HasSystemThreadingUsing(compilationUnit))
      {
         var usingDirective = SyntaxFactory.UsingDirective(
                                              SyntaxFactory.ParseName("System.Threading"))
                                           .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

         newRoot = newRoot.AddUsings(usingDirective);
      }

      var ctType = SyntaxFactory.IdentifierName("CancellationToken");
      var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"))
                                 .WithType(ctType);

      var newParamList = methodDecl.ParameterList.AddParameters(ctParam);
      var newMethod = methodDecl.WithParameterList(newParamList);

      newRoot = newRoot.ReplaceNode(methodDecl, newMethod);
      return document.WithSyntaxRoot(newRoot);
   }

   private static bool HasSystemThreadingUsing(CompilationUnitSyntax root)
   {
      foreach (var u in root.Usings)
      {
         if (u.Name is null)
         {
            continue;
         }

         var name = u.Name.ToString();
         if (name == "System.Threading")
         {
            return true;
         }
      }

      return false;
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
         var p = parameters[i];
         if (!string.Equals(p.Identifier.Text, ctSymbol.Name, StringComparison.Ordinal))
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

   // ----------------------------------------------------------------------
   // Syntax-level helpers – lambdas / anonymous functions
   // ----------------------------------------------------------------------

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
         var p = parameters[i];
         if (!string.Equals(p.Identifier.Text, ctSymbol.Name, StringComparison.Ordinal))
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
}