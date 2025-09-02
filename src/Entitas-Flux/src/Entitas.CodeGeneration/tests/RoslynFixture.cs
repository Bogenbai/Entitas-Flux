using DesperateDevs.Roslyn;
using Microsoft.CodeAnalysis;

namespace Entitas.CodeGeneration.Tests
{
	public sealed class RoslynFixture
	{
		public readonly string ProjectPath;
		public readonly INamedTypeSymbol[] Types;

		public RoslynFixture()
		{
			string root = TestExtensions.GetProjectRoot();
			ProjectPath = $"{root}/Tests/TestFixtures/TestFixtures.csproj";
			Types = new ProjectParser(ProjectPath).GetTypes();
		}
	}
}
