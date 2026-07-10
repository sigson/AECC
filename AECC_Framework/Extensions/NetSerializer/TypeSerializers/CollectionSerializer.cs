/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace NetSerializer
{
	/// <summary>
	/// Universal serializer for generic collections. Replaces the old DictionarySerializer,
	/// which only handled the exact Dictionary&lt;,&gt; type, and the implicit field-walking
	/// of List&lt;T&gt; by GenericSerializer.
	///
	/// Handles any concrete type that either implements IDictionary&lt;K,V&gt;
	/// (Dictionary, ConcurrentDictionary, SortedDictionary, SortedList, user subclasses
	/// and IDictionary-implementing wrappers), or exposes exactly one IEnumerable&lt;T&gt;
	/// together with a usable "add" path:
	///   public Add(T) / Enqueue(T) / Push(T), ICollection&lt;T&gt;.Add,
	///   or IProducerConsumerCollection&lt;T&gt;.TryAdd (ConcurrentQueue/Stack/Bag).
	/// Stack-like types (add via Push) are refilled in reverse to preserve order.
	///
	/// Wire format: uint (count + 1, 0 == null), then elements
	/// (for dictionaries: key, value pairs) written through the regular element writers.
	/// The collection is serialized by content only - extra fields declared on
	/// subclasses are NOT serialized.
	/// </summary>
	sealed class CollectionSerializer : IExpressionTypeSerializer
	{
		sealed class Info
		{
			public bool IsDictionary;
			public Type KeyType;
			public Type ValueType;
			public Type ElementType;            // for dictionaries: KeyValuePair<K,V>
			public Type EnumerableInterface;    // IEnumerable<ElementType>
			public Type EnumeratorInterface;    // IEnumerator<ElementType>
			public MethodInfo AddMethod;        // non-dictionary fill method
			public MethodInfo SetItemMethod;    // IDictionary<K,V>.set_Item
			public bool ReverseOnRead;          // stack-like (Push) semantics
			public ConstructorInfo Ctor0;
			public ConstructorInfo CtorCapacity; // only for exact List<> / Dictionary<,>
		}

		static readonly ConcurrentDictionary<Type, Info> s_cache = new ConcurrentDictionary<Type, Info>();

		static readonly MethodInfo s_arrayCopy =
			typeof(Array).GetMethod(nameof(Array.Copy), new[] { typeof(Array), typeof(Array), typeof(int) });

		static readonly MethodInfo s_moveNext =
			typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext));

		static readonly MethodInfo s_dispose =
			typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose));

		public bool Handles(Type type)
		{
			return Resolve(type) != null;
		}

		public IEnumerable<Type> GetSubtypes(Type type)
		{
			var info = Resolve(type);

			if (info.IsDictionary)
				return new[] { typeof(uint), info.KeyType, info.ValueType };

			return new[] { typeof(uint), info.ElementType };
		}

		static Info Resolve(Type type)
		{
			return s_cache.GetOrAdd(type, ResolveCore);
		}

		static Info ResolveCore(Type type)
		{
			if (type.IsArray || type.IsInterface || type.IsAbstract || type.IsEnum ||
				type == typeof(string) || !type.IsClass)
				return null;

			// Immutable collections implement ICollection<T> but throw on Add
			if (type.Namespace != null && type.Namespace.StartsWith("System.Collections.Immutable", StringComparison.Ordinal))
				return null;

			var ctor0 = type.GetConstructor(Type.EmptyTypes);
			if (ctor0 == null)
				return null;

			var interfaces = type.GetInterfaces();

			// ---- dictionary path ----
			var idicts = interfaces
				.Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
				.ToArray();

			if (idicts.Length > 1)
				return null; // ambiguous, fall back to GenericSerializer

			if (idicts.Length == 1)
			{
				var idict = idicts[0];
				var ga = idict.GetGenericArguments();
				var elem = typeof(KeyValuePair<,>).MakeGenericType(ga);

				return new Info
				{
					IsDictionary = true,
					KeyType = ga[0],
					ValueType = ga[1],
					ElementType = elem,
					EnumerableInterface = typeof(IEnumerable<>).MakeGenericType(elem),
					EnumeratorInterface = typeof(IEnumerator<>).MakeGenericType(elem),
					SetItemMethod = idict.GetProperty("Item").GetSetMethod(),
					Ctor0 = ctor0,
					CtorCapacity = GetCapacityCtor(type, typeof(Dictionary<,>)),
				};
			}

			// ---- flat collection path ----
			var ienums = interfaces
				.Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
				.ToArray();

			if (ienums.Length != 1)
				return null; // no or ambiguous element type

			var elemType = ienums[0].GetGenericArguments()[0];

			bool reverse = false;

			MethodInfo add = GetPublicInstanceMethod(type, "Add", elemType);

			if (add == null)
				add = GetPublicInstanceMethod(type, "Enqueue", elemType);

			if (add == null)
			{
				add = GetPublicInstanceMethod(type, "Push", elemType);
				reverse = add != null;
			}

			if (add == null)
			{
				var icoll = typeof(ICollection<>).MakeGenericType(elemType);
				if (icoll.IsAssignableFrom(type))
					add = icoll.GetMethod("Add");
			}

			if (add == null)
			{
				var ipcc = typeof(IProducerConsumerCollection<>).MakeGenericType(elemType);
				if (ipcc.IsAssignableFrom(type))
					add = ipcc.GetMethod("TryAdd");
			}

			if (add == null)
				return null;

			return new Info
			{
				IsDictionary = false,
				ElementType = elemType,
				EnumerableInterface = ienums[0],
				EnumeratorInterface = typeof(IEnumerator<>).MakeGenericType(elemType),
				AddMethod = add,
				ReverseOnRead = reverse,
				Ctor0 = ctor0,
				CtorCapacity = GetCapacityCtor(type, typeof(List<>)),
			};
		}

		static MethodInfo GetPublicInstanceMethod(Type type, string name, Type paramType)
		{
			try
			{
				return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance,
					null, new[] { paramType }, null);
			}
			catch (AmbiguousMatchException)
			{
				return null;
			}
		}

		static ConstructorInfo GetCapacityCtor(Type type, Type expectedGenTypeDef)
		{
			// Only use ctor(int) as a capacity hint for the exact well-known types,
			// where its meaning is guaranteed.
			if (!type.IsGenericType || type.GetGenericTypeDefinition() != expectedGenTypeDef)
				return null;

			return type.GetConstructor(new[] { typeof(int) });
		}

		public LambdaExpression GenerateWriterLambda(Serializer serializer, Type type)
		{
			var info = Resolve(type);
			var elem = info.ElementType;

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var v = Expression.Parameter(type, "value");

			var uintData = serializer.GetTypeData(typeof(uint));

			var bufType = elem.MakeArrayType();
			var buf = Expression.Variable(bufType, "buf");
			var tmp = Expression.Variable(bufType, "tmp");
			var n = Expression.Variable(typeof(int), "n");
			var i = Expression.Variable(typeof(int), "i");
			var en = Expression.Variable(info.EnumeratorInterface, "en");
			var cur = Expression.Variable(elem, "cur");

			var enumBrk = Expression.Label("enumEnd");
			var writeBrk = Expression.Label("writeEnd");

			var getEnumerator = info.EnumerableInterface.GetMethod("GetEnumerator");
			var currentGetter = info.EnumeratorInterface.GetProperty("Current").GetGetMethod();

			// grow buffer when full: tmp = new elem[n * 2]; Array.Copy(buf, tmp, n); buf = tmp;
			var grow = Expression.IfThen(
				Expression.Equal(n, Expression.ArrayLength(buf)),
				Expression.Block(
					Expression.Assign(tmp, Expression.NewArrayBounds(elem, Expression.Multiply(n, Expression.Constant(2)))),
					Expression.Call(s_arrayCopy, buf, tmp, n),
					Expression.Assign(buf, tmp)));

			// Snapshot the collection into a buffer first. This guarantees that the
			// written count always matches the number of written elements, even for
			// concurrent collections mutated during serialization (their Count and
			// their enumeration snapshot are not guaranteed to agree).
			var enumerate = Expression.Block(
				Expression.Assign(en, Expression.Call(v, getEnumerator)),
				Expression.TryFinally(
					Expression.Loop(
						Expression.IfThenElse(
							Expression.Call(en, s_moveNext),
							Expression.Block(
								Expression.Assign(cur, Expression.Call(en, currentGetter)),
								grow,
								Expression.Assign(Expression.ArrayAccess(buf, n), cur),
								Expression.PostIncrementAssign(n)),
							Expression.Break(enumBrk)),
						enumBrk),
					Expression.IfThen(
						Expression.NotEqual(en, Expression.Constant(null, info.EnumeratorInterface)),
						Expression.Call(en, s_dispose))));

			Expression writeElement;
			if (info.IsDictionary)
			{
				var item = Expression.ArrayAccess(buf, i);
				writeElement = Expression.Block(
					Helpers.WriteValue(serializer, info.KeyType, s, st, Expression.Property(item, "Key")),
					Helpers.WriteValue(serializer, info.ValueType, s, st, Expression.Property(item, "Value")));
			}
			else
			{
				writeElement = Helpers.WriteValue(serializer, elem, s, st, Expression.ArrayAccess(buf, i));
			}

			var writeAll = Expression.Block(
				new[] { buf, tmp, n, i, en, cur },
				Expression.Assign(buf, Expression.NewArrayBounds(elem, Expression.Constant(8))),
				Expression.Assign(n, Expression.Constant(0)),
				enumerate,
				// write count + 1
				Helpers.InvokeWriter(uintData, s, st,
					Expression.Convert(Expression.Add(n, Expression.Constant(1)), typeof(uint))),
				Expression.Assign(i, Expression.Constant(0)),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.LessThan(i, n),
						Expression.Block(
							writeElement,
							Expression.PostIncrementAssign(i)),
						Expression.Break(writeBrk)),
					writeBrk));

			var body = Expression.IfThenElse(
				Expression.Equal(v, Expression.Constant(null, type)),
				Helpers.InvokeWriter(uintData, s, st, Expression.Constant(0u)),
				writeAll);

			return Expression.Lambda(Helpers.GetWriterDelegateType(type), body, s, st, v);
		}

		public LambdaExpression GenerateReaderLambda(Serializer serializer, Type type)
		{
			var info = Resolve(type);

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var uintData = serializer.GetTypeData(typeof(uint));

			var n = Expression.Variable(typeof(uint), "n");
			var count = Expression.Variable(typeof(int), "count");
			var result = Expression.Variable(type, "result");

			Expression newInstance = info.CtorCapacity != null
				? Expression.New(info.CtorCapacity, count)
				: Expression.New(info.Ctor0);

			Expression fill = info.IsDictionary
				? BuildDictionaryFill(serializer, info, result, count, s, st)
				: (info.ReverseOnRead
					? BuildReverseFill(serializer, info, result, count, s, st)
					: BuildSequentialFill(serializer, info, result, count, s, st));

			var readAll = Expression.Block(
				Expression.Assign(count, Expression.Convert(Expression.Subtract(n, Expression.Constant(1u)), typeof(int))),
				Expression.Assign(result, newInstance),
				fill);

			var body = Expression.Block(
				new[] { n, count, result },
				Expression.Assign(n, Helpers.InvokeReader(uintData, s, st, typeof(uint))),
				Expression.IfThenElse(
					Expression.Equal(n, Expression.Constant(0u)),
					Expression.Assign(result, Expression.Constant(null, type)),
					readAll),
				result);

			return Expression.Lambda(Helpers.GetReaderDelegateType(type), body, s, st);
		}

		Expression BuildDictionaryFill(Serializer serializer, Info info, Expression result, Expression count,
			ParameterExpression s, ParameterExpression st)
		{
			var i = Expression.Variable(typeof(int), "i");
			var k = Expression.Variable(info.KeyType, "k");
			var val = Expression.Variable(info.ValueType, "val");
			var brk = Expression.Label("fillEnd");

			return Expression.Block(
				new[] { i, k, val },
				Expression.Assign(i, Expression.Constant(0)),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.LessThan(i, count),
						Expression.Block(
							Expression.Assign(k, Helpers.ReadValue(serializer, info.KeyType, s, st)),
							Expression.Assign(val, Helpers.ReadValue(serializer, info.ValueType, s, st)),
							// IDictionary<K,V>.set_Item: tolerant, works for Concurrent/Sorted/derived
							Expression.Call(result, info.SetItemMethod, k, val),
							Expression.PostIncrementAssign(i)),
						Expression.Break(brk)),
					brk));
		}

		Expression BuildSequentialFill(Serializer serializer, Info info, Expression result, Expression count,
			ParameterExpression s, ParameterExpression st)
		{
			var i = Expression.Variable(typeof(int), "i");
			var brk = Expression.Label("fillEnd");

			return Expression.Block(
				new[] { i },
				Expression.Assign(i, Expression.Constant(0)),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.LessThan(i, count),
						Expression.Block(
							Expression.Call(result, info.AddMethod,
								Helpers.ReadValue(serializer, info.ElementType, s, st)),
							Expression.PostIncrementAssign(i)),
						Expression.Break(brk)),
					brk));
		}

		Expression BuildReverseFill(Serializer serializer, Info info, Expression result, Expression count,
			ParameterExpression s, ParameterExpression st)
		{
			// Stack semantics: elements were written top-to-bottom (enumeration order);
			// push them back in reverse so the original order is restored.
			var elem = info.ElementType;
			var buf = Expression.Variable(elem.MakeArrayType(), "buf");
			var i = Expression.Variable(typeof(int), "i");
			var readBrk = Expression.Label("readEnd");
			var pushBrk = Expression.Label("pushEnd");

			return Expression.Block(
				new[] { buf, i },
				Expression.Assign(buf, Expression.NewArrayBounds(elem, count)),
				Expression.Assign(i, Expression.Constant(0)),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.LessThan(i, count),
						Expression.Block(
							Expression.Assign(Expression.ArrayAccess(buf, i),
								Helpers.ReadValue(serializer, elem, s, st)),
							Expression.PostIncrementAssign(i)),
						Expression.Break(readBrk)),
					readBrk),
				Expression.Assign(i, Expression.Subtract(count, Expression.Constant(1))),
				Expression.Loop(
					Expression.IfThenElse(
						Expression.GreaterThanOrEqual(i, Expression.Constant(0)),
						Expression.Block(
							Expression.Call(result, info.AddMethod, Expression.ArrayAccess(buf, i)),
							Expression.PostDecrementAssign(i)),
						Expression.Break(pushBrk)),
					pushBrk));
		}
	}
}
