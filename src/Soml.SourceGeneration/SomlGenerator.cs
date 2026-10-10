using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Soml.SourceGeneration;

/// <summary>
/// Writes the code to read and write the types of a <c>[SomlSerializable]</c> context, so that <c>SomlSerializer</c> needs no reflection.
/// </summary>
/// <remarks>
/// The generated code follows the reflection path of <c>SomlSerializer</c> rule for rule: which members a type has and in which order, how a constructor is chosen and its parameters bound, required members, nullable annotations, unknown keys, and the error messages. The scalar types, enums, collections, and dictionaries use the converters that the reflection path uses too.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SomlGenerator : IIncrementalGenerator
{
	private const string AttributeName = "Soml.Serialization.SomlSerializableAttribute";

	/// <inheritdoc/>
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var results = context.SyntaxProvider.ForAttributeWithMetadataName(
			AttributeName,
			static (node, _) => node is ClassDeclarationSyntax,
			static (syntaxContext, cancellationToken) => Emitter.Generate((INamedTypeSymbol)syntaxContext.TargetSymbol, syntaxContext.SemanticModel.Compilation, syntaxContext.Attributes, cancellationToken));

		context.RegisterSourceOutput(results, static (sourceContext, result) =>
		{
			if (result.Source is not null)
			{
				sourceContext.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
			}
		});

		// A diagnostic is reported in the syntax tree it is about, so that `#pragma warning disable` works for it. Only a context with diagnostics waits for the compilation.
		var diagnostics = results.Where(static result => !result.Diagnostics.IsEmpty).Select(static (result, _) => result.Diagnostics).Combine(context.CompilationProvider);

		context.RegisterSourceOutput(diagnostics, static (sourceContext, pair) =>
		{
			var trees = new Dictionary<string, SyntaxTree?>(StringComparer.Ordinal);

			foreach (var diagnostic in pair.Left)
			{
				SyntaxTree? tree = null;

				if (diagnostic.FilePath is { } path && !trees.TryGetValue(path, out tree))
				{
					tree = pair.Right.SyntaxTrees.FirstOrDefault(candidate => candidate.FilePath == path);
					trees.Add(path, tree);
				}

				sourceContext.ReportDiagnostic(diagnostic.ToDiagnostic(tree));
			}
		});
	}
}

/*
The result of one context, as values that compare by content, so that the incremental pipeline can tell when nothing changed. A diagnostic keeps its location as a path and spans, rather than as a `Location`, which holds a syntax tree.
*/
internal sealed record GenerationResult(string HintName, string? Source, EquatableArray<DiagnosticInfo> Diagnostics);

internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, string? FilePath, TextSpan Span, LinePositionSpan LineSpan, EquatableArray<string> Arguments)
{
	public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
	{
		var lineSpan = location?.GetLineSpan();
		return new DiagnosticInfo(descriptor, location?.SourceTree?.FilePath, location?.SourceSpan ?? default, lineSpan?.Span ?? default, new EquatableArray<string>([.. arguments]));
	}

	public Diagnostic ToDiagnostic(SyntaxTree? tree)
	{
		var location = tree is not null ? Location.Create(tree, Span) : FilePath is not null ? Location.Create(FilePath, Span, LineSpan) : Location.None;
		return Diagnostic.Create(Descriptor, location, [.. Arguments]);
	}
}

internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
	private readonly ImmutableArray<T> _items = items.IsDefault ? [] : items;

	public bool IsEmpty => _items.IsEmpty;

	// Note: Not using AsSpan().SequenceEqual because it was slower for identical backing arrays on .NET 10 ARM64 than ImmutableArray's existing SequenceEqual fast path for incremental cache hits.
	public bool Equals(EquatableArray<T> other) => _items.SequenceEqual(other._items);

	public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

	public override int GetHashCode()
	{
		// Note: Not using System.HashCode because it is unavailable to this generator's netstandard2.0 target.
		var hash = 17;

		foreach (var item in _items)
		{
			hash = unchecked((hash * 31) + (item?.GetHashCode() ?? 0));
		}

		return hash;
	}

	public ImmutableArray<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

	IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((System.Collections.IEnumerable)_items).GetEnumerator();
}

internal static class Descriptors
{
	public static readonly DiagnosticDescriptor UnsupportedType = new("SOML001", "Type not supported by the SOML source generator", "{0} is not supported by the SOML source generator: {1}. Use [SomlIgnore] on the member, or the reflection-based SomlSerializer overloads that take SomlSerializerOptions.", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor NotPartial = new("SOML002", "SOML serializer context must be partial", "The SOML serializer context {0} must be partial, and so must the types it is nested in, so that the source generator can add to it", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor NotContext = new("SOML003", "SOML serializer context must derive from SomlSerializerContext", "{0} has [SomlSerializable], so it must derive from SomlSerializerContext", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor NoConstructor = new("SOML004", "Type has no constructor to read it with", "{0} has no public parameterless constructor and not exactly one other public constructor, so reading it throws. Mark the constructor to read it with [SomlConstructor].", "Soml", DiagnosticSeverity.Warning, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor DuplicateTypeInfoName = new("SOML005", "Property name of a type is taken", "The SOML serializer context {0} cannot have a property named {1} for this type, because another type or a member of the context has that name. Set TypeInfoPropertyName on its [SomlSerializable] attribute.", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor NotAccessible = new("SOML007", "Type is private or protected", "{0} is private or protected, so the SOML serializer context cannot have a property for it. Make it internal or public.", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor SplitContext = new("SOML006", "SOML serializer context has attributes on more than one declaration", "Put all [SomlSerializable] attributes of the SOML serializer context {0} on one of its declarations", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor InvalidTypeInfoName = new("SOML008", "Property name is not a valid identifier", "The SOML serializer context {0} cannot have a property named '{1}', because it is not a valid C# identifier. Set TypeInfoPropertyName to a valid identifier.", "Soml", DiagnosticSeverity.Error, isEnabledByDefault: true);
}
