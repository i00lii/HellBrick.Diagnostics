﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HellBrick.Diagnostics.DeadCode;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestHelper;

namespace HellBrick.Diagnostics.Test
{
	[TestClass]
	public class UnusedSymbolTest : CodeFixVerifier
	{
		protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer() => new UnusedSymbolAnalyzer();
		protected override CodeFixProvider GetCSharpCodeFixProvider() => new UnusedSymbolCodeFixProvider();

		[TestMethod]
		public void UnusedPrivateMemberIsRemoved()
		{
			const string source =
@"public class C
{
	private int _number;
}";
			const string result =
@"public class C
{
}";
			VerifyCSharpFix( source, result );
		}

		[TestMethod]
		public void UnusedPublicMemberIsNotRemoved()
		{
			const string source =
@"public class C
{
	public void Whatever();
}";

			VerifyNoFix( source );
		}

		[TestMethod]
		public void UnusedInternalMemberIsRemoved()
		{
			const string source =
@"public class C
{
	internal string Text { get; set; }
}";
			const string result =
@"public class C
{
}";
			VerifyCSharpFix( source, result );
		}

		[TestMethod]
		public void UsedPrivateMemberIsNotRemoved()
		{
			const string source =
@"public class C
{
	private int _number;
	public C()
	{
		_number = 42;
	}
}";
			VerifyNoFix( source );
		}

		[TestMethod]
		public void UsedInternalMemberIsNotRemoved()
		{
			const string source1 =
@"public class C
{
	internal string Text { get; set; }
}";
			const string source2 =
@"public class D
{
	public string GetText() => new C().Text;
}";
			VerifyNoFix( source1, source2 );
		}

		[TestMethod]
		public void UnusedInternalMemberIsNotRemovedIfHasInternalsVisibleTo()
		{
			const string source =
@"using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo( ""Tests.dll"" )]

public class C
{
	internal string Text { get; set; }
}";
			VerifyNoFix( source );
		}

		[TestMethod]
		public void PrivateMemberUsedByAnotherFileOfPartialClassIsNotRemoved()
		{
			const string source1 =
@"public partial class C
{
	private int _number;
}";
			const string source2 =
@"public partial class C
{
	public int Number => _number;
}";
			VerifyNoFix( source1, source2 );
		}
	}
}