#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProtoBuf.BuildTools.Analyzers;
using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;

namespace ProtoBuf.CodeFixes
{
    /// <summary>
    /// Fixes PBN0029, a nullable member whose initializer turns a null into a value on
    /// deserialize, by removing the initializer.
    /// </summary>
    /// <remarks>
    /// The other remedy - resetting the member to null in a <c>[ProtoBeforeDeserialization]</c>
    /// callback, which keeps the initializer for instances built in code - is left to the author:
    /// it means finding or naming a method, and in a hierarchy it only works on the root.
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NullableMemberInitializedCodeFixProvider)), Shared]
    public class NullableMemberInitializedCodeFixProvider : CodeFixProvider
    {
        internal const string RemoveInitializerKey = "NullableMemberInitialized.RemoveInitializer";

        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DataContractAnalyzer.NullableMemberInitialized.Id);

        /// <inheritdoc/>
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                // the diagnostic sits on the member's name: a property, or a field's declarator
                var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
                var declaration = token.Parent;
                if (declaration is not (PropertyDeclarationSyntax { Initializer: not null } or VariableDeclaratorSyntax { Initializer: not null })) continue;

                context.RegisterCodeFix(CodeAction.Create(
                    title: $"Remove the initializer from '{token.ValueText}'",
                    createChangedDocument: _ => Task.FromResult(context.Document.WithSyntaxRoot(
                        root.ReplaceNode(declaration, NonNullableMemberCodeFixProvider.WithoutInitializer(declaration)))),
                    equivalenceKey: RemoveInitializerKey), diagnostic);
            }
        }
    }
}
