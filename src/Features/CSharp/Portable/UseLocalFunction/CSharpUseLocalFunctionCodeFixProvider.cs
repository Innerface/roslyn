﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseLocalFunction
{
    [ExportCodeFixProvider(LanguageNames.CSharp), Shared]
    internal class CSharpUseLocalFunctionCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        private static TypeSyntax s_voidType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        private static TypeSyntax s_objectType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.UseLocalFunctionDiagnosticId);

        protected override bool IncludeDiagnosticDuringFixAll(Diagnostic diagnostic)
            => diagnostic.Severity != DiagnosticSeverity.Hidden && !diagnostic.IsSuppressed;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(
                new MyCodeAction(c => FixAsync(context.Document, context.Diagnostics.First(), c)),
                context.Diagnostics);
            return SpecializedTasks.EmptyTask;
        }

        protected override async Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics, 
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var nodesFromDiagnostics = new List<(
                LocalDeclarationStatementSyntax declaration,
                AnonymousFunctionExpressionSyntax function,
                List<InvocationExpressionSyntax> invocations)>(diagnostics.Length);

            var nodesToTrack = new HashSet<SyntaxNode>();

            foreach (var diagnostic in diagnostics)
            {
                var localDeclaration = (LocalDeclarationStatementSyntax)diagnostic.AdditionalLocations[0].FindNode(cancellationToken);
                var anonymousFunction = (AnonymousFunctionExpressionSyntax)diagnostic.AdditionalLocations[1].FindNode(cancellationToken);

                var invocations = new List<InvocationExpressionSyntax>(diagnostic.AdditionalLocations.Count - 2);

                for (var i = 2; i < diagnostic.AdditionalLocations.Count; i++)
                {
                    invocations.Add((InvocationExpressionSyntax)diagnostic.AdditionalLocations[i].FindNode(getInnermostNodeForTie: true, cancellationToken));
                }

                nodesFromDiagnostics.Add((localDeclaration, anonymousFunction, invocations));

                nodesToTrack.Add(localDeclaration);
                nodesToTrack.Add(anonymousFunction);
                nodesToTrack.AddRange(invocations);
            }

            var root = editor.OriginalRoot;
            var currentRoot = root.TrackNodes(nodesToTrack);

            // Process declarations in reverse order so that we see the effects of nested
            // declarations befor processing the outer decls.
            foreach (var (localDeclaration, anonymousFunction, invocations) in nodesFromDiagnostics.OrderByDescending(nodes => nodes.function.SpanStart))
            {
                var delegateType = (INamedTypeSymbol)semanticModel.GetTypeInfo(anonymousFunction, cancellationToken).ConvertedType;
                var parameterList = GenerateParameterList(anonymousFunction, delegateType.DelegateInvokeMethod);

                var currentLocalDeclaration = currentRoot.GetCurrentNode(localDeclaration);
                var currentAnonymousFunction = currentRoot.GetCurrentNode(anonymousFunction);

                currentRoot = ReplaceAnonymousWithLocalFunction(
                    document.Project.Solution.Workspace, currentRoot,
                    currentLocalDeclaration, currentAnonymousFunction,
                    delegateType.DelegateInvokeMethod, parameterList);

                // these invocations might actually be inside the local function! so we have to do this separately
                currentRoot = ReplaceInvocations(
                    document.Project.Solution.Workspace, currentRoot,
                    delegateType.DelegateInvokeMethod, parameterList,
                    invocations.Select(node => currentRoot.GetCurrentNode(node)).ToImmutableArray());
            }

            editor.ReplaceNode(root, currentRoot);
        }

        private static SyntaxNode ReplaceAnonymousWithLocalFunction(
            Workspace workspace, SyntaxNode currentRoot,
            LocalDeclarationStatementSyntax localDeclaration, AnonymousFunctionExpressionSyntax anonymousFunction,
            IMethodSymbol delegateMethod, ParameterListSyntax parameterList)
        {
            var newLocalFunctionStatement = CreateLocalFunctionStatement(localDeclaration, anonymousFunction, delegateMethod, parameterList)
                .WithTriviaFrom(localDeclaration)
                .WithAdditionalAnnotations(Formatter.Annotation);

            var editor = new SyntaxEditor(currentRoot, workspace);
            editor.ReplaceNode(localDeclaration, newLocalFunctionStatement);

            var anonymousFunctionStatement = anonymousFunction.GetAncestor<StatementSyntax>();
            if (anonymousFunctionStatement != localDeclaration)
            {
                // This is the split decl+init form.  Remove the second statement as we're
                // merging into the first one.
                editor.RemoveNode(anonymousFunctionStatement);
            }

            return editor.GetChangedRoot();
        }

        private static SyntaxNode ReplaceInvocations(
            Workspace workspace, SyntaxNode currentRoot,
            IMethodSymbol delegateMethod, ParameterListSyntax parameterList,
            ImmutableArray<InvocationExpressionSyntax> invocations)
        {
            return currentRoot.ReplaceNodes(invocations, (_ /* nested invocations! */, invocation) =>
            {
                var directInvocation = invocation.Expression is MemberAccessExpressionSyntax memberAccessExpression // it's a .Invoke call
                    ? invocation.WithExpression(memberAccessExpression.Expression).WithTriviaFrom(invocation) // remove it
                    : invocation;

                return WithNewParameterNames(directInvocation, delegateMethod, parameterList);
            });
        }

        private static LocalFunctionStatementSyntax CreateLocalFunctionStatement(
            LocalDeclarationStatementSyntax localDeclaration,
            AnonymousFunctionExpressionSyntax anonymousFunction,
            IMethodSymbol delegateMethod,
            ParameterListSyntax parameterList)
        {
            var modifiers = anonymousFunction.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
                ? new SyntaxTokenList(anonymousFunction.AsyncKeyword)
                : default;

            var returnType = delegateMethod.GenerateReturnTypeSyntax();
            
            var identifier = localDeclaration.Declaration.Variables[0].Identifier;
            var typeParameterList = default(TypeParameterListSyntax);

            var constraintClauses = default(SyntaxList<TypeParameterConstraintClauseSyntax>);

            var body = anonymousFunction.Body.IsKind(SyntaxKind.Block)
                ? (BlockSyntax)anonymousFunction.Body
                : null;

            var expressionBody = anonymousFunction.Body is ExpressionSyntax expression
                ? SyntaxFactory.ArrowExpressionClause(((LambdaExpressionSyntax)anonymousFunction).ArrowToken, expression)
                : null;

            var semicolonToken = anonymousFunction.Body is ExpressionSyntax
                ? localDeclaration.SemicolonToken
                : default;

            return SyntaxFactory.LocalFunctionStatement(
                modifiers, returnType, identifier, typeParameterList, parameterList,
                constraintClauses, body, expressionBody, semicolonToken);
        }

        private static ParameterListSyntax GenerateParameterList(
            AnonymousFunctionExpressionSyntax anonymousFunction, IMethodSymbol delegateMethod)
        {
            var parameterList = TryGetOrCreateParameterList(anonymousFunction);
            int i = 0;

            return parameterList != null
                ? parameterList.ReplaceNodes(parameterList.Parameters, (parameterNode, _) => PromoteParameter(parameterNode, delegateMethod.Parameters.ElementAtOrDefault(i++)))
                : SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(delegateMethod.Parameters.Select(parameter =>
                    PromoteParameter(SyntaxFactory.Parameter(parameter.Name.ToIdentifierToken()), parameter))));

            ParameterSyntax PromoteParameter(ParameterSyntax parameterNode, IParameterSymbol delegateParameter)
            {
                // delegateParameter may be null, consider this case: Action x = (a, b) => { };
                // we will still fall back to object

                if (parameterNode.Type == null)
                {
                    parameterNode = parameterNode.WithType(delegateParameter?.Type.GenerateTypeSyntax() ?? s_objectType);
                }

                if (delegateParameter?.HasExplicitDefaultValue == true)
                {
                    parameterNode = parameterNode.WithDefault(GetDefaultValue(delegateParameter));
                }

                return parameterNode;
            }
        }

        private static ParameterListSyntax TryGetOrCreateParameterList(AnonymousFunctionExpressionSyntax anonymousFunction)
        {
            switch (anonymousFunction)
            {
                case SimpleLambdaExpressionSyntax simpleLambda:
                    return SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(simpleLambda.Parameter));
                case ParenthesizedLambdaExpressionSyntax parenthesizedLambda:
                    return parenthesizedLambda.ParameterList;
                case AnonymousMethodExpressionSyntax anonymousMethod:
                    return anonymousMethod.ParameterList; // may be null!
                default:
                    throw ExceptionUtilities.UnexpectedValue(anonymousFunction);
            }
        }

        private static InvocationExpressionSyntax WithNewParameterNames(InvocationExpressionSyntax invocation, IMethodSymbol method, ParameterListSyntax newParameterList)
        {
            return invocation.ReplaceNodes(invocation.ArgumentList.Arguments, (argumentNode, _) =>
            {
                if (argumentNode.NameColon == null)
                {
                    return argumentNode;
                }

                var parameterIndex = TryDetermineParameterIndex(argumentNode.NameColon, method);
                if (parameterIndex == -1)
                {
                    return argumentNode;
                }

                var newParameter = newParameterList.Parameters.ElementAtOrDefault(parameterIndex);
                if (newParameter == null || newParameter.Identifier.IsMissing)
                {
                    return argumentNode;
                }

                return argumentNode.WithNameColon(argumentNode.NameColon.WithName(SyntaxFactory.IdentifierName(newParameter.Identifier)));
            });
        }

        private static int TryDetermineParameterIndex(NameColonSyntax argumentNameColon, IMethodSymbol method)
        {
            var name = argumentNameColon.Name.Identifier.ValueText;
            return method.Parameters.IndexOf(p => p.Name == name);
        }

        private static EqualsValueClauseSyntax GetDefaultValue(IParameterSymbol parameter)
            => SyntaxFactory.EqualsValueClause(ExpressionGenerator.GenerateExpression(parameter.Type, parameter.ExplicitDefaultValue, canUseFieldReference: true));

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument) 
                : base(FeaturesResources.Use_local_function, createChangedDocument, FeaturesResources.Use_local_function)
            {
            }
        }
    }
}
