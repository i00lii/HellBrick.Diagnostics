﻿using System;
using System.Composition;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using HellBrick.Diagnostics.Utils;

namespace HellBrick.Diagnostics.ConfigureAwait
{
	[ExportCodeFixProvider( LanguageNames.CSharp, Name = nameof( TaskAwaiterCodeFixProvider ) ), Shared]
	public class TaskAwaiterCodeFixProvider : CodeFixProvider
	{
		private static readonly ArgumentListSyntax _configureAwaitArgList =
			ArgumentList
			(
				Token( SyntaxKind.OpenParenToken ),
				SeparatedList
				(
					new ArgumentSyntax[]
					{
						Argument( LiteralExpression( SyntaxKind.FalseLiteralExpression ) )
					}
				),
				Token( SyntaxKind.CloseParenToken )
			);

		public sealed override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create( TaskAwaiterAnalyzer.DiagnosticID );
		public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

		public sealed override Task RegisterCodeFixesAsync( CodeFixContext context )
		{
			CodeAction codeFix = CodeAction.Create( "Add 'ConfigureAwait'", c => AddConfigureAwaitAsync( context, c ), nameof( TaskAwaiterCodeFixProvider ) );
			context.RegisterCodeFix( codeFix, context.Diagnostics[ 0 ] );
			return TaskHelper.CompletedTask;
		}

		private async Task<Document> AddConfigureAwaitAsync( CodeFixContext context, CancellationToken cancellationToken )
		{
			SyntaxNode root = await context.Document.GetSyntaxRootAsync( context.CancellationToken ).ConfigureAwait( false );
			ExpressionSyntax awaitedExpression = root.FindNode( context.Span ) as ExpressionSyntax;

			InvocationExpressionSyntax configureAwaitInvocation =
				InvocationExpression
				(
					MemberAccessExpression
					(
						SyntaxKind.SimpleMemberAccessExpression,
						awaitedExpression,
						IdentifierName( "ConfigureAwait" )
					),
					_configureAwaitArgList
				);

			SyntaxNode newRoot = root.ReplaceNode( awaitedExpression, configureAwaitInvocation );
			return context.Document.WithSyntaxRoot( newRoot );
		}
	}
}