﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.MakeLocalFunctionStatic
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PassInCapturedVariablesAsArgumentsCodeFixProvider)), Shared]
    internal sealed class PassInCapturedVariablesAsArgumentsCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        //  "CS8421: A static local function can't contain a reference to <variable>."
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("CS8421");

        internal override CodeFixCategory CodeFixCategory => CodeFixCategory.Compile;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            return WrapFixAsync(
                context.Document,
                ImmutableArray.Create(diagnostic),
                (document, semanticModel, localFunction, captures, cancellationToken) =>
                {
                    context.RegisterCodeFix(
                        new MyCodeAction(c => MakeLocalFunctionStaticHelper.MakeLocalFunctionStaticAsync(
                            document,
                            semanticModel,
                            localFunction,
                            captures,
                            c)),
                        diagnostic);

                    return Task.CompletedTask;
                },
                context.CancellationToken);
        }

        protected override Task FixAllAsync(Document document, ImmutableArray<Diagnostic> diagnostics, SyntaxEditor editor, CancellationToken cancellationToken)
            => WrapFixAsync(
                document,
                diagnostics,
                (d, semanticModel, localFunction, captures, token) => MakeLocalFunctionStaticHelper.MakeLocalFunctionStaticAsync(
                        d,
                        semanticModel,
                        localFunction,
                        captures,
                        editor,
                        token),
                cancellationToken);

        private static async Task WrapFixAsync(
            Document document,
            ImmutableArray<Diagnostic> diagnostics,
            Func<Document, SemanticModel, LocalFunctionStatementSyntax, ImmutableArray<ISymbol>, CancellationToken, Task> fixer,
            CancellationToken cancellationToken)
        {
            var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;

            // Even when the language version doesn't support staic local function, the compiler will still
            // generate this error. So we need to check to make sure we don't provide incorrect fix.
            if (!MakeLocalFunctionStaticHelper.IsStaticLocalFunctionSupported(root.SyntaxTree))
            {
                return;
            }

            var localFunctions = diagnostics
                .Select(d => root.FindNode(d.Location.SourceSpan).AncestorsAndSelf().OfType<LocalFunctionStatementSyntax>().FirstOrDefault())
                .Where(n => n != null)
                .Distinct()
                .ToImmutableArrayOrEmpty();

            if (localFunctions.Length == 0)
            {
                return;
            }

            var semanticModel = (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;

            foreach (var localFunction in localFunctions)
            {

                if (MakeLocalFunctionStaticHelper.TryGetCaputuredSymbols(localFunction, semanticModel, out var captures) &&
                    MakeLocalFunctionStaticHelper.CanMakeLocalFunctionStatic(captures))
                {
                    await fixer(document, semanticModel, localFunction, captures, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(CSharpFeaturesResources.Pass_in_captured_variables_as_arguments, createChangedDocument, CSharpFeaturesResources.Pass_in_captured_variables_as_arguments)
            {
            }
        }
    }
}
