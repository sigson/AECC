/*
 * Copyright 2015 Tomi Valkeinen
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace NetSerializer
{
	static class Helpers
	{
		public static IEnumerable<FieldInfo> GetFieldInfos(Type type)
		{
			Debug.Assert(type.IsSerializable);

			var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
				.Where(fi => (fi.Attributes & FieldAttributes.NotSerialized) == 0)
				.OrderBy(f => f.Name, StringComparer.Ordinal);

			if (type.BaseType == null)
			{
				return fields;
			}
			else
			{
				var baseFields = GetFieldInfos(type.BaseType);
				return baseFields.Concat(fields);
			}
		}

		public static Type GetWriterDelegateType(Type type)
		{
			return typeof(SerializeDelegate<>).MakeGenericType(type);
		}

		public static Type GetReaderDelegateType(Type type)
		{
			return typeof(DeserializeDelegate<>).MakeGenericType(type);
		}

		static Expression ConvertIfNeeded(Expression expr, Type targetType)
		{
			return expr.Type == targetType ? expr : Expression.Convert(expr, targetType);
		}

		/// <summary>
		/// Build an expression that invokes the (possibly not-yet-generated) typed writer
		/// of the given TypeData. The delegate is loaded from the TypeData field at
		/// invocation time, so recursive/mutually-recursive type graphs work.
		/// </summary>
		public static Expression InvokeWriter(TypeData data, Expression serializer, Expression stream, Expression value)
		{
			var delType = GetWriterDelegateType(data.Type);

			var delExpr = Expression.Convert(
				Expression.Field(Expression.Constant(data), nameof(TypeData.WriterDelegate)),
				delType);

			return Expression.Invoke(delExpr, serializer, stream, ConvertIfNeeded(value, data.Type));
		}

		/// <summary>
		/// Build an expression that invokes the typed reader of the given TypeData and
		/// converts the result to wantedType.
		/// </summary>
		public static Expression InvokeReader(TypeData data, Expression serializer, Expression stream, Type wantedType)
		{
			var delType = GetReaderDelegateType(data.Type);

			var delExpr = Expression.Convert(
				Expression.Field(Expression.Constant(data), nameof(TypeData.ReaderDelegate)),
				delType);

			Expression call = Expression.Invoke(delExpr, serializer, stream);

			return ConvertIfNeeded(call, wantedType);
		}

		/// <summary>
		/// Write a value of the given static type, routing through the object serializer
		/// (type-id prefix) when the type cannot be called directly (polymorphism / null).
		/// </summary>
		public static Expression WriteValue(Serializer serializer, Type valueType, Expression serializerExpr, Expression stream, Expression value)
		{
			var data = serializer.GetIndirectData(valueType);
			return InvokeWriter(data, serializerExpr, stream, value);
		}

		/// <summary>
		/// Read a value of the given static type (see WriteValue).
		/// </summary>
		public static Expression ReadValue(Serializer serializer, Type valueType, Expression serializerExpr, Expression stream)
		{
			var data = serializer.GetIndirectData(valueType);
			return InvokeReader(data, serializerExpr, stream, valueType);
		}

		/// <summary>
		/// Wrap a static writer method into a SerializeDelegate&lt;type&gt;.
		/// Supported method shapes: (Stream, T) and (Serializer, Stream, T).
		/// </summary>
		public static Delegate WrapStaticWriter(Type type, MethodInfo mi)
		{
			if (mi == null)
				throw new ArgumentNullException(nameof(mi));

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var v = Expression.Parameter(type, "value");

			var ps = mi.GetParameters();

			Expression valueArg = ConvertIfNeeded(v, ps[ps.Length - 1].ParameterType);

			Expression call;
			if (ps.Length == 3)
				call = Expression.Call(mi, s, st, valueArg);
			else if (ps.Length == 2)
				call = Expression.Call(mi, st, valueArg);
			else
				throw new NotSupportedException(String.Format("Unsupported static writer shape: {0}.{1}", mi.DeclaringType, mi.Name));

			return Expression.Lambda(GetWriterDelegateType(type), call, s, st, v).Compile();
		}

		/// <summary>
		/// Wrap a static reader method into a DeserializeDelegate&lt;type&gt;.
		/// Supported method shapes:
		///   void (Stream, out T) / void (Serializer, Stream, out T)
		///   T (Stream)           / T (Serializer, Stream)
		/// </summary>
		public static Delegate WrapStaticReader(Type type, MethodInfo mi)
		{
			if (mi == null)
				throw new ArgumentNullException(nameof(mi));

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var ps = mi.GetParameters();

			Expression body;

			if (mi.ReturnType != typeof(void))
			{
				Expression call;
				if (ps.Length == 2)
					call = Expression.Call(mi, s, st);
				else if (ps.Length == 1)
					call = Expression.Call(mi, st);
				else
					throw new NotSupportedException(String.Format("Unsupported static reader shape: {0}.{1}", mi.DeclaringType, mi.Name));

				body = ConvertIfNeeded(call, type);
			}
			else
			{
				var byRefType = ps[ps.Length - 1].ParameterType;

				if (!byRefType.IsByRef)
					throw new NotSupportedException(String.Format("Unsupported static reader shape: {0}.{1}", mi.DeclaringType, mi.Name));

				var elemType = byRefType.GetElementType();
				var tmp = Expression.Variable(elemType, "tmp");

				Expression call;
				if (ps.Length == 3)
					call = Expression.Call(mi, s, st, tmp);
				else if (ps.Length == 2)
					call = Expression.Call(mi, st, tmp);
				else
					throw new NotSupportedException(String.Format("Unsupported static reader shape: {0}.{1}", mi.DeclaringType, mi.Name));

				body = Expression.Block(new[] { tmp }, call, ConvertIfNeeded(tmp, type));
			}

			return Expression.Lambda(GetReaderDelegateType(type), body, s, st).Compile();
		}

		/// <summary>
		/// Trampoline used by ObjectSerializer: (Serializer, Stream, object) -> cast -> typed writer
		/// </summary>
		public static SerializeDelegate<object> CreateWriterTrampoline(TypeData data)
		{
			if (data.Type == typeof(object))
				return (SerializeDelegate<object>)data.WriterDelegate;

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");
			var o = Expression.Parameter(typeof(object), "value");

			var body = InvokeWriter(data, s, st, o);

			return Expression.Lambda<SerializeDelegate<object>>(body, s, st, o).Compile();
		}

		/// <summary>
		/// Trampoline used by ObjectSerializer: typed reader -> boxed object
		/// </summary>
		public static DeserializeDelegate<object> CreateReaderTrampoline(TypeData data)
		{
			if (data.Type == typeof(object))
				return (DeserializeDelegate<object>)data.ReaderDelegate;

			var s = Expression.Parameter(typeof(Serializer), "serializer");
			var st = Expression.Parameter(typeof(Stream), "stream");

			var body = InvokeReader(data, s, st, typeof(object));

			return Expression.Lambda<DeserializeDelegate<object>>(body, s, st).Compile();
		}
	}
}
