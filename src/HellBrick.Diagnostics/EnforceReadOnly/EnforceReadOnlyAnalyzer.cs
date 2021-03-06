using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HellBrick.Diagnostics.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HellBrick.Diagnostics.EnforceReadOnly
{
	[DiagnosticAnalyzer( LanguageNames.CSharp )]
	public class EnforceReadOnlyAnalyzer : DiagnosticAnalyzer
	{
		public const string DiagnosticID = IDPrefix.Value + "EnforceReadOnly";

		private const string _title = "Field can be made read-only";
		private const string _messageFormat = "Field '{0}' can be made read-only";
		private const string _category = DiagnosticCategory.Design;

		private static readonly SpecialType[] _primitiveValueTypes =
		{
			SpecialType.System_Boolean,
			SpecialType.System_Byte,
			SpecialType.System_Int16,
			SpecialType.System_Int32,
			SpecialType.System_Int64,
			SpecialType.System_SByte,
			SpecialType.System_UInt16,
			SpecialType.System_UInt32,
			SpecialType.System_UInt64,
			SpecialType.System_Single,
			SpecialType.System_Double
		};

		private static readonly DiagnosticDescriptor _rule = new DiagnosticDescriptor( DiagnosticID, _title, _messageFormat, _category, DiagnosticSeverity.Warning, isEnabledByDefault: true );

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create( _rule );

		public override void Initialize( AnalysisContext context )
		{
			context.ConfigureGeneratedCodeAnalysis( GeneratedCodeAnalysisFlags.None );
			context.RegisterSemanticModelAction( EnforceReadOnlyOnClassFields );
		}

		private void EnforceReadOnlyOnClassFields( SemanticModelAnalysisContext context )
		{
			NonReadOnlyFieldFinder fieldFinder = new NonReadOnlyFieldFinder( context.SemanticModel, context.CancellationToken );
			var fieldCandidates = fieldFinder.RunAsync().GetAwaiter().GetResult();
			if ( fieldCandidates.Count == 0 )
				return;

			FieldWriteFinder writeFinder = new FieldWriteFinder( fieldCandidates, context.SemanticModel, context.CancellationToken );
			fieldCandidates = writeFinder.DiscardFieldsAssignedToAsync().GetAwaiter().GetResult();
			foreach ( var enforceableField in fieldCandidates )
				context.ReportDiagnostic( Diagnostic.Create( _rule, enforceableField.Locations[ 0 ], enforceableField.Name ) );
		}

		private class NonReadOnlyFieldFinder : CSharpSyntaxWalker
		{
			private readonly SemanticModel _semanticModel;
			private readonly CancellationToken _cancellationToken;
			private readonly HashSet<IFieldSymbol> _fields;

			public NonReadOnlyFieldFinder( SemanticModel semanticModel, CancellationToken cancellationToken )
			{
				_fields = new HashSet<IFieldSymbol>();

				_semanticModel = semanticModel;
				_cancellationToken = cancellationToken;
			}

			public async Task<HashSet<IFieldSymbol>> RunAsync()
			{
				var root = await _semanticModel.SyntaxTree.GetRootAsync( _cancellationToken ).ConfigureAwait( false );
				Visit( root );
				return _fields;
			}

			public override void VisitFieldDeclaration( FieldDeclarationSyntax node )
			{
				//	The declaration that declares multiple variable is ignored.
				if ( node.Declaration.Variables.Count != 1 )
					return;

				var fieldSymbol = _semanticModel.GetDeclaredSymbol( node.Declaration.Variables[ 0 ], _cancellationToken ) as IFieldSymbol;
				if ( ShouldIgnore( fieldSymbol ) )
					return;

				_fields.Add( fieldSymbol );
			}

			private bool ShouldIgnore( IFieldSymbol fieldSymbol )
			{
				return
					fieldSymbol.IsReadOnly ||
					fieldSymbol.IsConst ||
					fieldSymbol.IsExtern ||
					fieldSymbol.Type.IsValueType && !IsPrimitiveValueType( fieldSymbol.Type ) ||
					fieldSymbol.DeclaredAccessibility > Accessibility.Private;
			}

			private bool IsPrimitiveValueType( ITypeSymbol type ) => _primitiveValueTypes.Any( t => type.SpecialType == t );

			public override void DefaultVisit( SyntaxNode node )
			{
				switch ( node.Kind() )
				{
					//	These nodes can't possibly contain field declarations, so there's not need to dive inside them.
					case SyntaxKind.AttributeList:
					case SyntaxKind.BaseList:
					case SyntaxKind.PropertyDeclaration:
					case SyntaxKind.MethodDeclaration:
					case SyntaxKind.ConstructorDeclaration:
						break;

					default:
						base.DefaultVisit( node );
						break;
				}
			}
		}

		private class FieldWriteFinder : CSharpSyntaxWalker
		{
			private readonly HashSet<IFieldSymbol> _fieldCandidates;
			private readonly SemanticModel _semanticModel;
			private readonly CancellationToken _cancellationToken;

			public FieldWriteFinder( HashSet<IFieldSymbol> fieldCandidates, SemanticModel semanticModel, CancellationToken cancellationToken )
			{
				_fieldCandidates = fieldCandidates;
				_semanticModel = semanticModel;
				_cancellationToken = cancellationToken;
			}

			public async Task<HashSet<IFieldSymbol>> DiscardFieldsAssignedToAsync()
			{
				var root = await _semanticModel.SyntaxTree.GetRootAsync( _cancellationToken ).ConfigureAwait( false );
				Visit( root );
				return _fieldCandidates;
			}

			public override void DefaultVisit( SyntaxNode node )
			{
				if ( _fieldCandidates.Count > 0 )
					base.DefaultVisit( node );
			}

			public override void VisitAssignmentExpression( AssignmentExpressionSyntax node )
			{
				base.VisitAssignmentExpression( node );
				DiscardFieldFromExpression( node.Left );

				//	This is a tricky case.
				//	Expressions 'like x[ y ] = z' or 'x.Y = z' violate readonly modifier if x is an instance of a value type.
				//	GetSymbolInfo() is required because GetDeclaredSymbol() doesn't work here for some reason.
				var accessedExpression = TryGetAccessedExpression( node.Left );
				if ( accessedExpression != null )
				{
					var indexedSymbol = _semanticModel.GetSymbolInfo( accessedExpression ).Symbol as IFieldSymbol;
					var indexedFieldSymbol = indexedSymbol;
					if ( indexedFieldSymbol != null && indexedFieldSymbol.Type.IsValueType )
						DiscardFieldFromExpression( accessedExpression );
				}
			}

			private ExpressionSyntax TryGetAccessedExpression( ExpressionSyntax assignmentTarget )
			{
				switch ( assignmentTarget.Kind() )
				{
					case SyntaxKind.ElementAccessExpression:
						return ( assignmentTarget as ElementAccessExpressionSyntax ).Expression;

					case SyntaxKind.SimpleMemberAccessExpression:
						return ( assignmentTarget as MemberAccessExpressionSyntax ).Expression;

					case SyntaxKind.ConditionalAccessExpression:
						return ( assignmentTarget as ConditionalAccessExpressionSyntax ).Expression;

					default:
						return null;
				}
			}

			public override void VisitPrefixUnaryExpression( PrefixUnaryExpressionSyntax node )
			{
				base.VisitPrefixUnaryExpression( node );
				DiscardFieldFromExpression( node.Operand );
			}

			public override void VisitPostfixUnaryExpression( PostfixUnaryExpressionSyntax node )
			{
				base.VisitPostfixUnaryExpression( node );
				DiscardFieldFromExpression( node.Operand );
			}

			public override void VisitArgument( ArgumentSyntax node )
			{
				base.VisitArgument( node );

				if ( !node.RefOrOutKeyword.IsKind( SyntaxKind.None ) )
					DiscardFieldFromExpression( node.Expression );
			}

			private void DiscardFieldFromExpression( ExpressionSyntax node )
			{
				var fieldSymbol = _semanticModel.GetSymbolInfo( node ).Symbol as IFieldSymbol;
				if ( fieldSymbol == null || !_fieldCandidates.Contains( fieldSymbol ) || IsInsideConstructorOfTypeThatContainsSymbol( node, fieldSymbol ) )
					return;

				_fieldCandidates.Remove( fieldSymbol );
			}

			private bool IsInsideConstructorOfTypeThatContainsSymbol( ExpressionSyntax node, IFieldSymbol fieldSymbol )
			{
				foreach ( var currentNode in node.Ancestors() )
				{
					switch ( currentNode.Kind() )
					{
						case SyntaxKind.ConstructorDeclaration:
							{
								var constructorSymbol = _semanticModel.GetDeclaredSymbol( currentNode );
								return constructorSymbol.ContainingType == fieldSymbol.ContainingType && constructorSymbol.IsStatic == fieldSymbol.IsStatic;
							}

						case SyntaxKind.ParenthesizedLambdaExpression:
						case SyntaxKind.SimpleLambdaExpression:
						case SyntaxKind.AnonymousMethodExpression:
						case SyntaxKind.MethodDeclaration:
							return false;

						default:
							continue;
					}
				}

				return false;
			}
		}
	}
}
