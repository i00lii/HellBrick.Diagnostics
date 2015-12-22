﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HellBrick.Diagnostics.Utils
{
	public static partial class SyntaxRootExtensions
	{
		public static bool IsAutoGenerated( this SyntaxNode root ) => HasGeneratedCodeAttribute( root ) || HasAutoGeneratedComment( root );

		private static bool HasAutoGeneratedComment( SyntaxNode root ) =>
			root.DescendantTrivia().Any
			(
				t =>
				t.ToString().StartsWith( "// <auto-generated" )
			);

		private static bool HasGeneratedCodeAttribute( SyntaxNode root )
		{
			return root
				.DescendantNodes()
				.OfType<AttributeSyntax>()
				.Any( attribute => attribute.Name.ToString().Contains( "GeneratedCode" ) );
		}
	}
}
