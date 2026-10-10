using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Soml.Serialization;

namespace Soml;

/*
A class, a struct, or a record as a SOML object, with a member for each public property and field. The members of a base class come first, and within each class, the properties in the order of their declaration, then the fields.

It is created with its constructor: the public parameterless one, or the one with `[SomlConstructor]`, or the only public one, such as the primary constructor of a record. Each parameter reads the key of the member with the same name, ignoring case. The other members are set after it, when the document has them.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal sealed class MembersConverter : Converter
{
	private readonly Type _type;
	private readonly ConstructorInvoker? _constructor;
	private readonly Parameter[] _parameters;
	private readonly Member[] _members;

	/*
	The keys that the type reads, members first, in the order that a suggestion for a misspelled key prefers them.

	Note: Not using FrozenSet because it can reorder equally close candidates, changing their suggestion priority.
	*/
	private readonly HashSet<string>? _knownKeys;

	private sealed record Member(string Key, string Name, Type Type, Func<object, object?> Get, Action<object, object?>? Set, bool IsNullable, bool IsRequired)
	{
		public bool IsSetByConstructor { get; set; }
	}

	private readonly record struct Parameter(string Key, Type Type, bool IsNullable, bool HasDefaultValue, object? DefaultValue, bool IsIgnored);

	public MembersConverter(Type type, SomlSerializerOptions options)
	{
		_type = type;
		var nullability = new NullabilityInfoContext();
		// Required-member attributes belong to their declaring members, including those inherited from a base class.

		var constructor = FindConstructor(type);
		_constructor = constructor is null ? null : ConstructorInvoker.Create(constructor);
		var parameterInfos = constructor?.GetParameters() ?? [];
		// Note: Not using GetProperties/GetFields because typed discovery saved allocations for mixed models but increased them for field-heavy DTOs with a static constant.
		var reflectedMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Instance);

		// Reflection keeps a covariant override's base property, but gives it the overriding getter. Only the property that declares that getter is read and written.
		var visibleMembers = reflectedMembers.Where(member => member is PropertyInfo or FieldInfo).Where(member => member is not PropertyInfo { GetMethod: { } getter } || getter.DeclaringType == member.DeclaringType).ToArray();

		// Reflection does not inherit attributes through a covariant override. Only its original properties need to be searched for inherited attributes, nearest first.
		var covariantProperties = reflectedMembers.OfType<PropertyInfo>().Where(property => property.GetMethod is { } getter && getter.DeclaringType != property.DeclaringType).ToArray();

		T? GetAttribute<T>(MemberInfo member) where T : Attribute
		{
			var attribute = member.GetCustomAttribute<T>();

			if (attribute is not null || member is not PropertyInfo { GetMethod: { } getter })
			{
				return attribute;
			}

			foreach (var property in covariantProperties)
			{
				if (property.GetMethod == getter && property.GetCustomAttribute<T>() is { } inherited)
				{
					return inherited;
				}
			}

			return null;
		}

		var members = new List<Member>(visibleMembers.Length);
		var parametersByName = parameterInfos.Length == 0 ? null : new Dictionary<string, ParameterInfo>(parameterInfos.Length, StringComparer.OrdinalIgnoreCase);

		foreach (var parameter in parameterInfos)
		{
			parametersByName!.TryAdd(parameter.Name!, parameter);
		}

		var membersByName = parameterInfos.Length == 0 ? null : new Dictionary<string, Member>(visibleMembers.Length, StringComparer.OrdinalIgnoreCase);
		var ignoredNames = parameterInfos.Length == 0 ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var declaringDepths = new Dictionary<Type, int>();
		var depth = 0;

		for (var current = type; current is not null; current = current.BaseType)
		{
			declaringDepths.Add(current, depth++);
		}

		var memberInfos = visibleMembers
			.Where(member =>
			{
				if (GetAttribute<SomlIgnoreAttribute>(member) is null)
				{
					return true;
				}

				ignoredNames?.Add(member.Name);
				return false;
			})
			.Where(member => (member is PropertyInfo { CanRead: true } property && property.GetIndexParameters().Length == 0 && property.GetMethod!.IsPublic) || member is FieldInfo)
			// A ref struct, such as the `Span` of a `Memory<T>`, cannot be read through reflection, and it is not a value to write.
			.Where(member => !(member is PropertyInfo { PropertyType.IsByRefLike: true } || member is FieldInfo { FieldType.IsByRefLike: true }))
			// Note: Not using OrderBy with a tuple sort key because it reduced allocations but slowed cold serialization of the larger model in local tests.
			.OrderByDescending(member => declaringDepths[member.DeclaringType!])
			.ThenBy(member => member is PropertyInfo ? 0 : 1)
			.ThenBy(member => member.MetadataToken);

		// A member without annotations, in code that has nullable reference types off, allows null.
		Member CreateProperty(PropertyInfo property, string key, bool isRequired)
		{
			// Note: Not using PropertyInfo.GetValue/SetValue because they wrap accessor failures in TargetInvocationException.
			var getter = MethodInvoker.Create(property.GetMethod!);
			var setter = property.SetMethod is { IsPublic: true } method ? MethodInvoker.Create(method) : null;
			return new Member(key, property.Name, property.PropertyType, target => getter.Invoke(target), setter is null ? null : (target, value) => setter.Invoke(target, value), Converter.CanBeNull(property.PropertyType) && nullability.Create(property).WriteState != NullabilityState.NotNull, isRequired);
		}

		foreach (var memberInfo in memberInfos)
		{
			// `[SomlName]` on the constructor parameter of a member, as on the positional parameter of a record, names the member too.
			ParameterInfo? parameterInfo = null;
			parametersByName?.TryGetValue(memberInfo.Name, out parameterInfo);
			var key = SomlMetadata.Key(options, memberInfo.Name, GetAttribute<SomlNameAttribute>(memberInfo)?.Name ?? parameterInfo?.GetCustomAttribute<SomlNameAttribute>()?.Name);
			var isRequired = memberInfo.IsDefined(typeof(RequiredMemberAttribute));

			Member member = memberInfo switch
			{
				PropertyInfo property => CreateProperty(property, key, isRequired),
				FieldInfo field => new Member(
					key,
					field.Name,
					field.FieldType,
					field.GetValue,
					field.IsInitOnly ? null : field.SetValue,
					Converter.CanBeNull(field.FieldType) && nullability.Create(field).WriteState != NullabilityState.NotNull,
					isRequired),
				_ => throw new InvalidOperationException(),
			};

			members.Add(member);
			membersByName?.TryAdd(member.Name, member);
		}

		_members = [.. members];
		if (_members.Length > 1)
		{
			SomlMetadata.CheckUniqueKeys(type, [.. _members.Select(member => member.Name)], [.. _members.Select(member => member.Key)]);
		}

		var parameters = new Parameter[parameterInfos.Length];
		var parameterKeys = new HashSet<string>(parameterInfos.Length, StringComparer.Ordinal);

		for (var index = 0; index < parameterInfos.Length; index++)
		{
			var parameterInfo = parameterInfos[index];
			membersByName!.TryGetValue(parameterInfo.Name!, out var member);
			var isIgnored = member is null && ignoredNames!.Contains(parameterInfo.Name!);
			var key = isIgnored ? "" : member?.Key ?? SomlMetadata.Key(options, parameterInfo.Name!, parameterInfo.GetCustomAttribute<SomlNameAttribute>()?.Name);
			var hasDefaultValue = parameterInfo.IsOptional && parameterInfo.HasDefaultValue;
			var defaultValue = hasDefaultValue ? parameterInfo.DefaultValue : null;
			var defaultType = Nullable.GetUnderlyingType(parameterInfo.ParameterType) ?? parameterInfo.ParameterType;

			// Reflection gives a nullable enum's default as its underlying integer, which cannot be passed to the nullable parameter.
			if (defaultValue is not null && defaultType.IsEnum)
			{
				defaultValue = Enum.ToObject(defaultType, defaultValue);
			}

			if (defaultValue is null && (hasDefaultValue || isIgnored) && parameterInfo.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameterInfo.ParameterType) is null)
			{
				defaultValue = RuntimeHelpers.GetUninitializedObject(parameterInfo.ParameterType);
			}

			var parameter = new Parameter(
				key,
				parameterInfo.ParameterType,
				Converter.CanBeNull(parameterInfo.ParameterType) && nullability.Create(parameterInfo).WriteState != NullabilityState.NotNull,
				hasDefaultValue,
				defaultValue,
				// Without a matching nonignored member, a parameter of a member with `[SomlIgnore]`, as on a record's positional parameter, gets its default.
				IsIgnored: isIgnored);

			if (!parameter.IsIgnored)
			{
				parameterKeys.Add(key);
			}

			parameters[index] = parameter;
		}

		SomlMetadata.CheckUniqueParameterKeys(type, [.. parameters.Where(parameter => !parameter.IsIgnored).Select(parameter => parameter.Key)]);
		_parameters = parameters;

		foreach (var member in _members)
		{
			member.IsSetByConstructor = parameterKeys.Contains(member.Key);
		}

		_knownKeys = options.RejectUnknownKeys ? [.. _members.Select(member => member.Key).Concat(parameterKeys)] : null;
	}

	private static ConstructorInfo? FindConstructor(Type type)
	{
		var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
		ConstructorInfo? marked = null;
		ConstructorInfo? parameterless = null;

		foreach (var constructor in constructors)
		{
			if (constructor.IsDefined(typeof(SomlConstructorAttribute)))
			{
				if (marked is not null)
				{
					throw new SomlException($"{DescribeType(type)} has more than one constructor with [SomlConstructor]");
				}

				marked = constructor;
			}

			// A compiler-generated record copy constructor is non-public, so it is already excluded.
			if (constructor.GetParameters().Length == 0)
			{
				parameterless = constructor;
			}
		}

		if (marked is not null || parameterless is not null || constructors.Length == 1)
		{
			return marked ?? parameterless ?? constructors[0];
		}

		// A struct without a constructor is created as its default value.
		return null;
	}

	public override object? Read(SomlValue value, SerializerState state)
	{
		var members = state.ReadObject(value, _type);

		if (_constructor is null && !_type.IsValueType)
		{
			throw SomlMetadata.NoConstructor(state, _type);
		}

		if (_knownKeys is not null)
		{
			state.CheckUnknownKeys(members, _knownKeys, _type);
		}

		// Most constructors fit in inline span storage, avoiding a temporary argument array.
		// Note: Not using fixed-arity ConstructorInvoker.Invoke overloads because the span overload already dispatches up to four arguments to its fixed-arity fast path.
		Span<object?> arguments = _parameters.Length <= 4 ? [null, null, null, null] : new object?[_parameters.Length];

		for (var index = 0; index < _parameters.Length; index++)
		{
			var parameter = _parameters[index];

			if (!parameter.IsIgnored && members.TryGetValue(parameter.Key, out var member))
			{
				state.Push(parameter.Key);
				arguments[index] = state.Read(member, parameter.Type, parameter.IsNullable);
				state.Pop();
			}
			else if (parameter.HasDefaultValue || parameter.IsIgnored)
			{
				// An omitted value-type parameter gets its zero value, as `default` does, without running a struct's parameterless constructor.
				arguments[index] = parameter.DefaultValue;
			}
			else if (parameter.IsNullable)
			{
				arguments[index] = null;
			}
			else
			{
				throw state.MissingKey(parameter.Key, _type);
			}
		}

		object result;

		try
		{
			result = _constructor is null ? Activator.CreateInstance(_type)! : _constructor.Invoke(arguments[.._parameters.Length]);
		}
		catch (Exception exception)
		{
			throw state.ConstructorFailed(_type, exception);
		}

		foreach (var member in _members)
		{
			if (member.IsRequired && !members.ContainsKey(member.Key))
			{
				throw state.MissingKey(member.Key, _type);
			}

			// A member that the constructor sets, such as the property of a record, is not set again.
			// A member without a public setter, such as a computed property, is written but not read, as in System.Text.Json, so that what is written reads back.
			if (member.IsSetByConstructor || member.Set is null || !members.TryGetValue(member.Key, out var memberValue))
			{
				continue;
			}

			state.Push(member.Key);

			var memberResult = state.Read(memberValue, member.Type, member.IsNullable);

			try
			{
				member.Set(result, memberResult);
			}
			catch (Exception exception)
			{
				throw state.SetterFailed(member.Name, _type, exception);
			}

			state.Pop();
		}

		return result;
	}

	public override SomlValue Write(object value, SerializerState state)
	{
		var result = new SomlObject(_members.Length);

		foreach (var member in _members)
		{
			object? memberValue;

			try
			{
				memberValue = member.Get(value);
			}
			catch (Exception exception)
			{
				state.Push(member.Key);
				throw state.GetterFailed(member.Name, _type, exception);
			}

			if (memberValue is null && state.Options.OmitNullMembers)
			{
				continue;
			}

			state.Push(member.Key);
			result.Append(member.Key, state.Write(memberValue, member.Type));
			state.Pop();
		}

		return result;
	}
}
