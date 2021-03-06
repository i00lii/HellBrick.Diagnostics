﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HellBrick.Diagnostics.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace HellBrick.Diagnostics.DeadCode
{
	[ExportCodeFixProvider( LanguageNames.CSharp, Name = nameof( UnusedParameterCodeFixProvider ) ), Shared]
	public class UnusedParameterCodeFixProvider : CodeFixProvider
	{
		public sealed override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create( UnusedParameterAnalyzer.ID );
		public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

		public sealed override Task RegisterCodeFixesAsync( CodeFixContext context )
		{
			CodeAction codeFix = CodeAction.Create( "Remove unused parameter", ct => FixSolutionAsync( context.Document, context.Span, ct ) );
			context.RegisterCodeFix( codeFix, context.Diagnostics[ 0 ] );
			return TaskHelper.CompletedTask;
		}

		private async Task<Solution> FixSolutionAsync( Document document, TextSpan span, CancellationToken cancellationToken )
		{
			SyntaxNode root = await document.GetSyntaxRootAsync( cancellationToken ).ConfigureAwait( false );
			ParameterSyntax parameter = root.FindNode( span ) as ParameterSyntax;
			ParameterListSyntax parameterList = parameter.Parent as ParameterListSyntax;
			int parameterIndex = parameterList.Parameters.IndexOf( parameter );
			BaseMethodDeclarationSyntax methodDeclaration = parameter.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
			SemanticModel semanticModel = await document.GetSemanticModelAsync( cancellationToken ).ConfigureAwait( false );
			IMethodSymbol methodSymbol = semanticModel.GetDeclaredSymbol( methodDeclaration );
			Solution solution = document.Project.Solution;
			IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync( methodSymbol, solution, cancellationToken ).ConfigureAwait( false );

			IEnumerable<IChange> callSiteChanges =
				from caller in callers
				from location in caller.Locations
				where location.IsInSource
				select new CallSiteChange( location, parameterIndex, parameter.Identifier.ValueText ) into change
				where change.ReplacedNode != null
				select change;

			IEnumerable<IChange> allChanges
				= Enumerable
				.Repeat( new DeclarationChange( parameterList, parameter ), 1 )
				.Concat( callSiteChanges );

			solution
				= allChanges
				.GroupBy( change => change.ReplacedNode.SyntaxTree )
				.Aggregate
				(
					solution,
					( oldSolution, syntaxTreeChangeGroup ) =>
					{
						SyntaxTree syntaxTree = syntaxTreeChangeGroup.Key;
						Dictionary<SyntaxNode, IChange> changeLookup = syntaxTreeChangeGroup.ToDictionary( change => change.ReplacedNode );
						SyntaxNode oldRoot = syntaxTree.GetRoot( cancellationToken );
						SyntaxNode newRoot
							= oldRoot
							.ReplaceNodes
							(
								changeLookup.Keys,
								( originalNode, rewrittenNode ) => changeLookup[ originalNode ].ComputeReplacementNode( rewrittenNode )
							);

						DocumentId documentID = oldSolution.GetDocumentId( syntaxTree );
						return oldSolution.WithDocumentSyntaxRoot( documentID, newRoot );
					}
				);

			return solution;
		}

		private interface IChange
		{
			SyntaxNode ReplacedNode { get; }
			SyntaxNode ComputeReplacementNode( SyntaxNode replacedNode );
		}

		private class DeclarationChange : IChange
		{
			private readonly ParameterSyntax _parameter;
			private readonly ParameterListSyntax _parameterList;

			public DeclarationChange( ParameterListSyntax parameterList, ParameterSyntax parameter )
			{
				_parameterList = parameterList;
				_parameter = parameter;
			}

			public SyntaxNode ReplacedNode => _parameterList;
			public SyntaxNode ComputeReplacementNode( SyntaxNode replacedNode )
				=> _parameterList
				.WithParameters( _parameterList.Parameters.Remove( _parameter ) ) // there's no way for the parameter list to be rewritten by any other changes
				.WithAdditionalAnnotations( Formatter.Annotation );
		}

		private class CallSiteChange : IChange
		{
			private readonly int _parameterIndex;
			private readonly string _parameterName;

			public CallSiteChange( Location location, int parameterIndex, string parameterName )
			{
				_parameterIndex = parameterIndex;
				_parameterName = parameterName;

				ArgumentListSyntax argumentList
					= location.SourceTree.GetRoot().FindNode( location.SourceSpan )
					.AncestorsAndSelf()
					.Select( ancestor => TryGetArgumentList( ancestor ) )
					.Where( argList => argList != null )
					.FirstOrDefault();

				/// It's possible to have <see cref="argumentList"/> without finding a corresponding argument inside.
				/// This happens when the parameter is optional and not passed to the method.
				if ( argumentList != null && FindArgument( argumentList ) != null )
					ReplacedNode = argumentList;
			}

			private ArgumentListSyntax TryGetArgumentList( SyntaxNode ancestor )
				=> ( ancestor as InvocationExpressionSyntax )?.ArgumentList
				?? ( ancestor as ConstructorInitializerSyntax )?.ArgumentList;

			public SyntaxNode ReplacedNode { get; }
			public SyntaxNode ComputeReplacementNode( SyntaxNode replacedNode ) => RemoveArgument( replacedNode as ArgumentListSyntax );

			private SyntaxNode RemoveArgument( ArgumentListSyntax argumentList )
				=> argumentList.WithArguments( argumentList.Arguments.Remove( FindArgument( argumentList ) ) )
				.WithAdditionalAnnotations( Formatter.Annotation );

			private ArgumentSyntax FindArgument( ArgumentListSyntax argumentList )
				=> argumentList.Arguments.FirstOrDefault( arg => arg.NameColon?.Name.Identifier.ValueText == _parameterName )
				?? ( argumentList.Arguments.Count > _parameterIndex ? argumentList.Arguments[ _parameterIndex ] : null );
		}
	}
}