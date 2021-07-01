﻿using System.Collections.Generic;
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

namespace Obsessive.Defender.HeapAnalysis
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AvoidAllocationWithArrayEmptyCodeFix)), Shared]
    public class AvoidAllocationWithArrayEmptyCodeFix : CodeFixProvider
    {
        private const string RemoveUnnecessaryListCreation = "Avoid allocation by using Array.Empty<>()";

        public sealed override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(ExplicitAllocationAnalyzer.NewObjectRule.Id, ExplicitAllocationAnalyzer.NewArrayRule.Id);

        public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            var node = root.FindNode(diagnosticSpan);

            if (IsReturnStatement(node))
            {
                await TryToRegisterCodeFixesForReturnStatement(context, node, diagnostic);
                return;
            }

            if (IsMethodInvocationParameter(node))
            {
                await TryToRegisterCodeFixesForMethodInvocationParameter(context, node, diagnostic);
                return;
            }
        }

        private async Task TryToRegisterCodeFixesForMethodInvocationParameter(CodeFixContext context, SyntaxNode node, Diagnostic diagnostic)
        {
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken);
            if (IsExpectedParameterReadonlySequence(node, semanticModel) && node is ArgumentSyntax argument)
            {
                TryRegisterCodeFix(context, node, diagnostic, argument.Expression, semanticModel);
            }
        }

        private async Task TryToRegisterCodeFixesForReturnStatement(CodeFixContext context, SyntaxNode node, Diagnostic diagnostic)
        {
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken);

            if (IsInsideMemberReturningEnumerable(node, semanticModel))
            {
                TryRegisterCodeFix(context, node, diagnostic, node, semanticModel);
            }
        }

        private void TryRegisterCodeFix(CodeFixContext context, SyntaxNode node, Diagnostic diagnostic, SyntaxNode creationExpression, SemanticModel semanticModel)
        {
            switch (creationExpression)
            {
                case ObjectCreationExpressionSyntax objectCreation:
                    {
                        if (CanBeReplaceWithEnumerableEmpty(objectCreation, semanticModel))
                        {
                            if (objectCreation.Type is GenericNameSyntax genericName)
                            {
                                var codeAction = CodeAction.Create(RemoveUnnecessaryListCreation,
                                    token => Transform(context.Document, node, genericName.TypeArgumentList.Arguments[0], token),
                                    RemoveUnnecessaryListCreation);
                                context.RegisterCodeFix(codeAction, diagnostic);
                            }
                        }
                    }
                    break;
                case ArrayCreationExpressionSyntax arrayCreation:
                    {
                        if (CanBeReplaceWithEnumerableEmpty(arrayCreation))
                        {
                            var codeAction = CodeAction.Create(RemoveUnnecessaryListCreation,
                                token => Transform(context.Document, node, arrayCreation.Type.ElementType, token),
                                RemoveUnnecessaryListCreation);
                            context.RegisterCodeFix(codeAction, diagnostic);
                        }
                    }
                    break;
            }
        }


        private bool IsMethodInvocationParameter(SyntaxNode node) => node is ArgumentSyntax;

        private static bool IsReturnStatement(SyntaxNode node)
        {
            return node.Parent is ReturnStatementSyntax || node.Parent is YieldStatementSyntax || node.Parent is ArrowExpressionClauseSyntax;
        }

        private bool IsInsideMemberReturningEnumerable(SyntaxNode node, SemanticModel semanticModel)
        {
            return IsInsideMethodReturningEnumerable(node, semanticModel) ||
                   IsInsidePropertyDeclaration(node, semanticModel);

        }

        private bool IsInsidePropertyDeclaration(SyntaxNode node, SemanticModel semanticModel)
        {
            if (node.FindContainer<PropertyDeclarationSyntax>() is PropertyDeclarationSyntax propertyDeclaration && IsPropertyTypeReadonlySequence(semanticModel, propertyDeclaration))
            {
                return IsAutoPropertyWithGetter(node) || IsArrowExpression(node);
            }

            return false;
        }

        private bool IsAutoPropertyWithGetter(SyntaxNode node)
        {
            if (node.FindContainer<AccessorDeclarationSyntax>() is AccessorDeclarationSyntax accessorDeclaration)
            {
                return accessorDeclaration.Keyword.Text == "get";
            }

            return false;
        }

        private bool IsArrowExpression(SyntaxNode node)
        {
            return node.FindContainer<ArrowExpressionClauseSyntax>() != null;
        }

        private bool CanBeReplaceWithEnumerableEmpty(ArrayCreationExpressionSyntax arrayCreation)
        {
            return IsInitializationBlockEmpty(arrayCreation.Initializer);
        }

        private bool CanBeReplaceWithEnumerableEmpty(ObjectCreationExpressionSyntax objectCreation, SemanticModel semanticModel)
        {
            return IsCollectionType(semanticModel, objectCreation) &&
                   IsInitializationBlockEmpty(objectCreation.Initializer) &&
                   IsCopyConstructor(semanticModel, objectCreation) == false;
        }

        private static bool IsInsideMethodReturningEnumerable(SyntaxNode node, SemanticModel semanticModel)
        {
            if (node.FindContainer<MethodDeclarationSyntax>() is MethodDeclarationSyntax methodDeclaration)
            {
                if (IsReturnTypeReadonlySequence(semanticModel, methodDeclaration))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<Document> Transform(Document contextDocument, SyntaxNode node, TypeSyntax typeArgument, CancellationToken cancellationToken)
        {
            var noAllocation = SyntaxFactory.ParseExpression($"Array.Empty<{typeArgument}>()");
            var newNode = ReplaceExpression(node, noAllocation);
            if (newNode == null)
            {
                return contextDocument;
            }
            var syntaxRootAsync = await contextDocument.GetSyntaxRootAsync(cancellationToken);
            var newSyntaxRoot = syntaxRootAsync.ReplaceNode(node.Parent, newNode);
            return contextDocument.WithSyntaxRoot(newSyntaxRoot);
        }

        private SyntaxNode ReplaceExpression(SyntaxNode node, ExpressionSyntax newExpression)
        {
            switch (node.Parent)
            {
                case ReturnStatementSyntax parentReturn:
                    return parentReturn.WithExpression(newExpression);
                case ArrowExpressionClauseSyntax arrowStatement:
                    return arrowStatement.WithExpression(newExpression);
                case ArgumentListSyntax argumentList:
                    var newArguments = argumentList.Arguments.Select(x => x == node ? SyntaxFactory.Argument(newExpression) : x);
                    return argumentList.WithArguments(SyntaxFactory.SeparatedList(newArguments));
                default:
                    return null;
            }
        }

        private bool IsCopyConstructor(SemanticModel semanticModel, ObjectCreationExpressionSyntax objectCreation)
        {
            if (objectCreation.ArgumentList == null || objectCreation.ArgumentList.Arguments.Count == 0)
            {
                return false;
            }

            if (semanticModel.GetSymbolInfo(objectCreation).Symbol is IMethodSymbol methodSymbol)
            {
                if (methodSymbol.Parameters.Any(x => x.Type is INamedTypeSymbol namedType && IsCollectionType(namedType)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsInitializationBlockEmpty(InitializerExpressionSyntax initializer)
        {
            return initializer == null || initializer.Expressions.Count == 0;
        }

        private bool IsCollectionType(SemanticModel semanticModel, ObjectCreationExpressionSyntax objectCreationExpressionSyntax)
        {
            return semanticModel.GetTypeInfo(objectCreationExpressionSyntax).Type is INamedTypeSymbol createdType &&
                   (createdType.TypeKind == TypeKind.Array || IsCollectionType(createdType));
        }

        private bool IsCollectionType(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.ConstructedFrom.Interfaces.Any(x =>
                x.IsGenericType && x.ToString().StartsWith("System.Collections.Generic.ICollection"));
        }

        private static bool IsPropertyTypeReadonlySequence(SemanticModel semanticModel, PropertyDeclarationSyntax propertyDeclaration)
        {
            return IsTypeReadonlySequence(semanticModel, propertyDeclaration.Type);
        }

        private static bool IsReturnTypeReadonlySequence(SemanticModel semanticModel, MethodDeclarationSyntax methodDeclarationSyntax)
        {
            var typeSyntax = methodDeclarationSyntax.ReturnType;
            return IsTypeReadonlySequence(semanticModel, typeSyntax);
        }

        private bool IsExpectedParameterReadonlySequence(SyntaxNode node, SemanticModel semanticModel)
        {
            if (node is ArgumentSyntax argument && node.Parent is ArgumentListSyntax argumentList)
            {
                var argumentIndex = argumentList.Arguments.IndexOf(argument);
                if (semanticModel.GetSymbolInfo(argumentList.Parent).Symbol is IMethodSymbol methodSymbol)
                {
                    if (methodSymbol.Parameters.Length > argumentIndex)
                    {
                        var parameterType = methodSymbol.Parameters[argumentIndex].Type;
                        if (IsTypeReadonlySequence(semanticModel, parameterType))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsTypeReadonlySequence(SemanticModel semanticModel, TypeSyntax typeSyntax)
        {
            var returnType = ModelExtensions.GetTypeInfo(semanticModel, typeSyntax).Type;
            return IsTypeReadonlySequence(semanticModel, returnType);
        }

        private static bool IsTypeReadonlySequence(SemanticModel semanticModel, ITypeSymbol type)
        {
            if (type.Kind == SymbolKind.ArrayType)
            {
                return true;
            }

            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                foreach (var readonlySequence in GetReadonlySequenceTypes(semanticModel))
                {
                    if (readonlySequence.Equals(namedType.ConstructedFrom, SymbolEqualityComparer.Default))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<INamedTypeSymbol> GetReadonlySequenceTypes(SemanticModel semanticModel)
        {
            yield return semanticModel.Compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
            yield return semanticModel.Compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
            yield return semanticModel.Compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyCollection`1");
        }
    }
}
