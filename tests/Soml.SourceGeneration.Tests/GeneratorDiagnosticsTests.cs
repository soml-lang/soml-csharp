using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Soml.SourceGeneration.Tests;

public sealed class GeneratorDiagnosticsTests
{
	private static readonly ImmutableArray<MetadataReference> References =
	[
		.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
		MetadataReference.CreateFromFile(typeof(SomlSerializer).Assembly.Location),
	];

	private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

	private static CSharpCompilation Compile(string source) => CSharpCompilation.Create(
		"Test",
		[CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Test.cs")],
		References,
		new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

	/*
	Runs the generator, and gives its diagnostics and those of the compilation with the generated code, which must have no errors of its own.
	*/
	private static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compilation, string Source) Run(string source)
	{
		var driver = CSharpGeneratorDriver.Create([new SomlGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
		driver.RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out var generatorDiagnostics);
		var generated = output.SyntaxTrees.Skip(1).Select(tree => tree.ToString());
		return (generatorDiagnostics, output.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).ToImmutableArray(), string.Concat(generated));
	}

	private static Diagnostic Single(string source, string id)
	{
		var diagnostic = Assert.Single(Run(source).Generator);
		Assert.Equal(id, diagnostic.Id);
		return diagnostic;
	}

	[Theory]
	[InlineData("")]
	[InlineData("two names")]
	[InlineData("a-b")]
	[InlineData("123")]
	[InlineData("Name; public int Injected")]
	public void ReportsInvalidTypeInfoPropertyNamesWithoutEmittingInvalidCode(string name)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml.Serialization;

			[SomlSerializable(typeof(int), TypeInfoPropertyName = {{SymbolDisplay.FormatLiteral(name, quote: true)}})]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Equal("SOML008", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Fact]
	public void EscapedTypeInfoNamesCannotBypassExistingMemberChecks()
	{
		var (generator, compilation, _) = Run("""
			using Soml.Serialization;

			[SomlSerializable(typeof(int), TypeInfoPropertyName = "@Existing")]
			public partial class Context : SomlSerializerContext
			{
				public int Existing => 0;
			}
			""");

		Assert.Equal("SOML005", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("De\u200cfault")]
	[InlineData("Exi\u200csting")]
	public void IdentifierFormattingCharactersCannotBypassMemberChecks(string name)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml.Serialization;

			[SomlSerializable(typeof(int), TypeInfoPropertyName = {{SymbolDisplay.FormatLiteral(name, quote: true)}})]
			public partial class Context : SomlSerializerContext
			{
				public int Existing => 0;
			}
			""");

		Assert.Equal("SOML005", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("Missing")]
	[InlineData("global::System.Collections.Generic.List<Missing>")]
	public void UnresolvedMemberTypesLeaveOnlyTheOriginalCompilerError(string type)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml.Serialization;

			public sealed class Config
			{
				public {{type}} Value => throw new global::System.NotImplementedException();
			}

			[SomlSerializable(typeof(int))]
			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Equal("CS0246", Assert.Single(compilation).Id);
	}

	[Fact]
	public void FindsCollectionFactoriesInKeywordNamespaces()
	{
		var (generator, compilation, _) = Run("""
			using System.Collections;
			using System.Collections.Generic;
			using Soml.Serialization;

			namespace @event;

			public sealed class Items<T> : IEnumerable<T>
			{
				private Items() {}
				public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)new T[0]).GetEnumerator();
				IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
				internal static Items<T> Create() => new();
			}

			public static class Items
			{
				public static Items<T> CreateRange<T>(IEnumerable<T> items) => Items<T>.Create();
			}

			[SomlSerializable(typeof(Items<int>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("Converter0")]
	[InlineData("Converter0Implementation")]
	public void ReportsAnExistingContextMemberThatConflictsWithGeneratedMembers(string name)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml.Serialization;

			public sealed class Config;

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext
			{
				private sealed class {{name}};
			}
			""");

		Assert.Equal("SOML005", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Fact]
	public void ReportsAnExistingPropertyThatConflictsWithAConverter()
	{
		var (generator, compilation, _) = Run("""
			using Soml.Serialization;

			public sealed class Config;

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext
			{
				public int Converter0 => 0;
			}
			""");

		Assert.Equal("SOML005", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Fact]
	public void ReportsAnExistingNestedTypeThatConflictsWithARootProperty()
	{
		var (generator, compilation, _) = Run("""
			using Soml.Serialization;

			[SomlSerializable(typeof(int), TypeInfoPropertyName = "Existing")]
			public partial class Context : SomlSerializerContext
			{
				private sealed class Existing;
			}
			""");

		Assert.Equal("SOML005", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Fact]
	public void GeneratesCodeThatCompilesWithoutWarnings()
	{
		var (generator, compilation, source) = Run("""
			using System.Collections.Generic;
			using Soml.Serialization;

			namespace App;

			public sealed record Server(string Host, int Port = 80);

			public sealed class Config
			{
				public required string Name { get; init; }

				public List<Server> Servers { get; set; } = [];

				public Dictionary<string, int?> Limits { get; set; } = [];

				public Config? Parent { get; set; }
			}

			public static partial class Outer
			{
				[SomlSerializable(typeof(Config))]
				internal partial class Context : SomlSerializerContext;
			}
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
		Assert.StartsWith("// <auto-generated/>", source.TrimStart('﻿'), StringComparison.Ordinal);
		Assert.Contains("public global::Soml.Serialization.SomlTypeInfo<global::App.Config> Config", source, StringComparison.Ordinal);
	}

	[Fact]
	public void EscapesKeywordNamesInContextsAndAccessors()
	{
		var (generator, compilation, _) = Run("""
			using Soml.Serialization;

			public sealed class Box<@class>
			{
				public @class? Value { get; init; }
			}

			public static partial class @namespace
			{
				[SomlSerializable(typeof(Box<string>), TypeInfoPropertyName = "class")]
				public partial class @event : SomlSerializerContext;
			}
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("public object Value { get; set; } = new();", "The member Value of Config", "object")]
	[InlineData("public Dictionary<int, string> Map { get; set; } = [];", "The member Map of Config", "does not have string keys")]
	[InlineData("public int[,] Grid { get; set; } = new int[0, 0];", "The member Grid of Config", "multidimensional")]
	[InlineData("public IDisposable? Resource { get; set; }", "The member Resource of Config", "is an abstract class, an interface")]
	[InlineData("public System.Collections.ArrayList List { get; set; } = [];", "The member List of Config", "non-generic collection")]
	[InlineData("public List<object> Items { get; set; } = [];", "The member Items of Config", "object is written")]
	public void ReportsAnUnsupportedMember(string member, string subject, string reason)
	{
		var diagnostic = Single($$"""
			using System;
			using System.Collections.Generic;
			using Soml.Serialization;

			public sealed class Config
			{
				{{member}}
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""", "SOML001");

		var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
		Assert.StartsWith(subject, message, StringComparison.Ordinal);
		Assert.Contains(reason, message, StringComparison.Ordinal);
		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains(member.Split(" {")[0].Split(' ')[^1], diagnostic.Location.SourceTree!.ToString().Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("public static void Add(int item) {}")]
	[InlineData("public void Add<TUnused>(int item) {}")]
	[InlineData("public void Add(ref int item) {}")]
	[InlineData("public void Add(in int item) {}")]
	[InlineData("public void Add(long item) {}")]
	public void RejectsCollectionsWithoutAnOrdinaryInstanceAdd(string add)
	{
		var (generator, compilation, _) = Run($$"""
			using System.Collections;
			using System.Collections.Generic;
			using Soml.Serialization;

			public sealed class Items : IEnumerable<int>
			{
				{{add}}
				public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)new int[0]).GetEnumerator();
				IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
			}

			[SomlSerializable(typeof(Items))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Equal("SOML001", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("ref")]
	[InlineData("out")]
	[InlineData("in")]
	[InlineData("ref readonly")]
	public void SkipsByReferenceCollectionConstructors(string modifier)
	{
		var (generator, compilation, source) = Run($$"""
			using System;
			using System.Collections.Generic;
			using Soml.Serialization;

			public sealed class Items : List<int>
			{
				public Items() {}
				public Items({{modifier}} IEnumerable<int> items)
				{
					throw new InvalidOperationException("By-reference constructor must not be called");
				}
			}

			[SomlSerializable(typeof(Items))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
		Assert.Contains("var result = new global::Items();", source, StringComparison.Ordinal);
	}

	[Fact]
	public void ReportsAContextThatIsNotPartial()
	{
		Single("""
			using Soml.Serialization;

			[SomlSerializable(typeof(int))]
			public class Context : SomlSerializerContext
			{
				public Context() : base(null)
				{
				}
			}
			""", "SOML002");

		Single("""
			using Soml.Serialization;

			public class Outer
			{
				[SomlSerializable(typeof(int))]
				public partial class Context : SomlSerializerContext;
			}
			""", "SOML002");
	}

	[Fact]
	public void ReportsAContextThatDoesNotDeriveFromTheBase()
	{
		Single("""
			using Soml.Serialization;

			[SomlSerializable(typeof(int))]
			public partial class Context;
			""", "SOML003");
	}

	[Fact]
	public void WarnsAboutATypeWithoutAConstructorToReadItWith()
	{
		var diagnostic = Single("""
			using Soml.Serialization;

			public sealed class Config
			{
				public Config(int a)
				{
				}

				public Config(string b)
				{
				}
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""", "SOML004");

		Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
	}

	[Fact]
	public void ReportsTwoTypesWithTheSamePropertyName()
	{
		Single("""
			using Soml.Serialization;

			namespace A
			{
				public sealed class Config;
			}

			namespace B
			{
				public sealed class Config;
			}

			[SomlSerializable(typeof(A.Config))]
			[SomlSerializable(typeof(B.Config))]
			public partial class Context : SomlSerializerContext;
			""", "SOML005");
	}

	[Theory]
	[InlineData("Options")]
	[InlineData("Default")]
	[InlineData("Existing")]
	public void ReportsAPropertyNameThatIsTaken(string name)
	{
		var diagnostic = Single($$"""
			using Soml.Serialization;

			public sealed class {{name}}
			{
				public int A { get; set; }
			}

			[SomlSerializable(typeof({{name}}))]
			public partial class Context : SomlSerializerContext
			{
				public static int Existing => 1;
			}
			""", "SOML005");

		Assert.Contains(name, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Converter0")]
	[InlineData("Converter0Implementation")]
	public void ReportsAPropertyNameThatConflictsWithGeneratedMembers(string name)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml.Serialization;

			public sealed class {{name}};

			[SomlSerializable(typeof({{name}}))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Equal("SOML005", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("_converter0")]
	[InlineData("_typeInfo0")]
	[InlineData("_default")]
	public void FieldBackedPropertiesFreeUpFormerBackingFieldNames(string name)
	{
		var (generator, compilation, source) = Run($$"""
			using Soml.Serialization;

			public sealed class {{name}};

			[SomlSerializable(typeof({{name}}))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
		Assert.Contains("get => field ??=", source, StringComparison.Ordinal);
	}

	[Fact]
	public void CanRenameAPropertyThatWouldConflictWithAGeneratedMember()
	{
		var (generator, compilation, _) = Run("""
			using Soml.Serialization;

			public sealed class Converter0;

			[SomlSerializable(typeof(Converter0), TypeInfoPropertyName = "Config")]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
	}

	[Fact]
	public void ReportsAttributesOnTwoDeclarationsOfTheContext()
	{
		var (generator, _, _) = Run("""
			using Soml.Serialization;

			public sealed class A;

			public sealed class B;

			[SomlSerializable(typeof(A))]
			public partial class Context : SomlSerializerContext;

			[SomlSerializable(typeof(B))]
			public partial class Context;
			""");

		Assert.NotEmpty(generator);
		Assert.All(generator, diagnostic => Assert.Equal("SOML006", diagnostic.Id));
	}

	[Fact]
	public void AllowsACollectionCycleThroughAnObject()
	{
		var (generator, compilation, source) = Run("""
			using System.Collections.Generic;
			using Soml.Serialization;

			public sealed class Node
			{
				public List<Node> Children { get; set; } = [];
			}

			[SomlSerializable(typeof(List<Node>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
		Assert.NotEmpty(source);
	}

	[Theory]
	[InlineData("public (int Count, string Name) Pair { get; set; }", "is a tuple")]
	[InlineData("public Tree Tree { get; set; } = [];", "holds itself")]
	[InlineData("public Child Child { get; set; } = new();", "constraints")]
	[InlineData("public Box<string> Box { get; set; } = new() { Value = \"\" };", "constraints")]
	[InlineData("public Node<int>? Node { get; set; }", "without end")]
	public void ReportsATypeThatCannotBeGenerated(string member, string reason)
	{
		var diagnostic = Single($$"""
			using System.Collections.Generic;
			using Soml.Serialization;

			public sealed class Tree : List<Tree>;

			public class Base<T> where T : class
			{
				public T? Value { get; init; }
			}

			public sealed class Child : Base<string>;

			public sealed class Box<T> where T : class
			{
				public required T Value { get; set; }
			}

			public sealed class Node<T>
			{
				public Node<List<T>>? Child { get; set; }
			}

			public sealed class Config
			{
				{{member}}
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""", "SOML001");

		Assert.Contains(reason, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("in")]
	[InlineData("ref")]
	[InlineData("ref readonly")]
	[InlineData("out")]
	public void ReportsAConstructorParameterPassedByReference(string modifier)
	{
		// The generated code would not compile for `ref` and `out`, and the reflection path cannot read any of them, as the parameter's type is a by-ref type.
		var diagnostic = Single($$"""
			using Soml.Serialization;

			public sealed class Config
			{
				public Config({{modifier}} int a)
				{
					{{(modifier == "out" ? "a = 1;" : "")}}
				}

				public int A { get; set; }
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""", "SOML001");

		Assert.Contains("passed by reference", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
	}

	[Fact]
	public void RequiresACollectionFactoryResultToBeAssignableWithoutAUserDefinedConversion()
	{
		var (generator, compilation, _) = Run("""
			using System.Collections;
			using System.Collections.Generic;
			using Soml.Serialization;

			namespace App;

			public sealed class Items<T> : IEnumerable<T>
			{
				private Items()
				{
				}

				public static implicit operator Items<T>(ConvertedItems<T> items) => new();

				public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)new T[0]).GetEnumerator();

				IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
			}

			public sealed class ConvertedItems<T>;

			public static class Items
			{
				public static ConvertedItems<T> CreateRange<T>(IEnumerable<T> items) => new();
			}

			[SomlSerializable(typeof(Items<int>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Equal("SOML001", Assert.Single(generator).Id);
		Assert.Empty(compilation);
	}

	[Fact]
	public void ReportsAPrivateType()
	{
		Single("""
			using System.Collections.Generic;
			using Soml.Serialization;

			public partial class Outer
			{
				private sealed class Hidden
				{
					public int A { get; set; }
				}

				[SomlSerializable(typeof(List<Hidden>))]
				internal partial class Context : SomlSerializerContext;
			}
			""", "SOML007");
	}

	[Fact]
	public void GivesAnInternalTypeAnInternalProperty()
	{
		var (generator, compilation, source) = Run("""
			using Soml.Serialization;

			internal sealed class Config
			{
				public int A { get; set; }
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
		Assert.Contains("internal global::Soml.Serialization.SomlTypeInfo<global::Config> Config", source, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("internal")]
	[InlineData("protected internal")]
	public void AllowsRootTypesAccessibleWithinTheAssembly(string accessibility)
	{
		var (generator, compilation, source) = Run($$"""
			using Soml.Serialization;

			public class Outer
			{
				{{accessibility}} sealed class Config
				{
					public int Value { get; set; }
				}
			}

			[SomlSerializable(typeof(Outer.Config))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
		Assert.Contains("internal global::Soml.Serialization.SomlTypeInfo<global::Outer.Config> Config", source, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("DateTime")]
	[InlineData("DateTime?")]
	public void GeneratesDateTimeConstructorDefaults(string parameterType)
	{
		var (generator, compilation, _) = Run($$"""
			using System;
			using System.Runtime.CompilerServices;
			using System.Runtime.InteropServices;
			using Soml.Serialization;

			public sealed class Config
			{
				public Config([Optional, DateTimeConstant(1234567)] {{parameterType}} time)
				{
					Time = time;
				}

				public {{parameterType}} Time { get; }
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
	}

	[Fact]
	public void ReportsTwoConstructorsWithTheAttribute()
	{
		Single("""
			using Soml;
			using Soml.Serialization;

			public sealed class Config
			{
				[SomlConstructor]
				public Config(int a)
				{
				}

				[SomlConstructor]
				public Config(string b)
				{
				}
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""", "SOML001");
	}

	[Theory]
	[InlineData("public")]
	[InlineData("internal")]
	[InlineData("protected internal")]
	public void RejectsConstructorsWithOverlappingClosedParameterTypes(string accessibility)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml;
			using Soml.Serialization;

			public class Config<T>
			{
				[SomlConstructor]
				public Config(T value)
				{
					Value = value;
				}

				{{accessibility}} Config(int value)
				{
					Value = default!;
				}

				public T Value { get; }
			}

			[SomlSerializable(typeof(Config<int>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Contains("overlapping parameter types", Assert.Single(generator).GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
		Assert.Equal("SOML001", generator[0].Id);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("string", "public", "")]
	[InlineData("int", "private", "")]
	[InlineData("int", "public", ", int extra = 0")]
	[InlineData("int", "public", ", params int[] extra")]
	public void AllowsClosedGenericConstructorsWithoutOverlappingSignatures(string typeArgument, string accessibility, string extraParameter)
	{
		var (generator, compilation, _) = Run($$"""
			using Soml;
			using Soml.Serialization;

			public class Config<T>
			{
				[SomlConstructor]
				public Config(T value)
				{
					Value = value;
				}

				{{accessibility}} Config(int value{{extraParameter}})
				{
					Value = default!;
				}

				public T Value { get; }
			}

			[SomlSerializable(typeof(Config<{{typeArgument}}>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("collection", "public")]
	[InlineData("collection", "internal")]
	[InlineData("collection", "protected internal")]
	[InlineData("dictionary", "public")]
	[InlineData("dictionary", "internal")]
	[InlineData("dictionary", "protected internal")]
	public void RejectsCollectionConstructorsWithOverlappingClosedParameterTypes(string kind, string accessibility)
	{
		var baseType = kind == "collection" ? "List<T>" : "Dictionary<string, T>";
		var genericParameter = kind == "collection" ? "IEnumerable<T>" : "IDictionary<string, T>";
		var concreteParameter = kind == "collection" ? "IEnumerable<int>" : "IDictionary<string, int>";
		var (generator, compilation, _) = Run($$"""
			using System.Collections.Generic;
			using Soml.Serialization;

			public class Items<T> : {{baseType}}
			{
				public Items({{genericParameter}} items) : base(items)
				{
				}

				{{accessibility}} Items({{concreteParameter}} items)
				{
				}
			}

			[SomlSerializable(typeof(Items<int>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Contains("overlapping parameter types", Assert.Single(generator).GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
		Assert.Equal("SOML001", generator[0].Id);
		Assert.Empty(compilation);
	}

	[Theory]
	[InlineData("collection", "string", "public", "")]
	[InlineData("collection", "int", "private", "")]
	[InlineData("collection", "int", "public", ", int extra = 0")]
	[InlineData("dictionary", "string", "public", "")]
	[InlineData("dictionary", "int", "private", "")]
	[InlineData("dictionary", "int", "public", ", int extra = 0")]
	public void AllowsCollectionConstructorsWithoutOverlappingClosedSignatures(string kind, string typeArgument, string accessibility, string extraParameter)
	{
		var baseType = kind == "collection" ? "List<T>" : "Dictionary<string, T>";
		var genericParameter = kind == "collection" ? "IEnumerable<T>" : "IDictionary<string, T>";
		var concreteParameter = kind == "collection" ? "IEnumerable<int>" : "IDictionary<string, int>";
		var (generator, compilation, _) = Run($$"""
			using System.Collections.Generic;
			using Soml.Serialization;

			public class Items<T> : {{baseType}}
			{
				public Items({{genericParameter}} items) : base(items)
				{
				}

				{{accessibility}} Items({{concreteParameter}} items{{extraParameter}})
				{
				}
			}

			[SomlSerializable(typeof(Items<{{typeArgument}}>))]
			public partial class Context : SomlSerializerContext;
			""");

		Assert.Empty(generator);
		Assert.Empty(compilation);
	}

	[Fact]
	public void CachesTheOutputWhenAnUnrelatedFileChanges()
	{
		var compilation = Compile("""
			using Soml.Serialization;

			public sealed class Config
			{
				public int A { get; set; }
			}

			[SomlSerializable(typeof(Config))]
			public partial class Context : SomlSerializerContext;
			""");

		GeneratorDriver driver = CSharpGeneratorDriver.Create([new SomlGenerator().AsSourceGenerator()], parseOptions: ParseOptions, driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
		driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
		driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("public sealed class Unrelated;", ParseOptions, path: "Other.cs", cancellationToken: TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);

		var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs);
		Assert.All(outputs, output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, output.Reason.ToString()));
	}
}
